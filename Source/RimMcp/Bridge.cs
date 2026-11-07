using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Verse;

namespace RimMcp
{
    // Local HTTP endpoint for the MCP server (Mcp/rimmcp_server.py).
    //   GET  /health                -> {"ok":true,...}
    //   POST /call {"tool","args"}  -> tool result
    // Bound to 127.0.0.1 only; every request needs the bearer token written to
    // <save data folder>/RimMcp/endpoint.json. Game state is touched only on the main
    // thread: the listener thread queues a request and waits for Pump.Update to run it.
    public static class Bridge
    {
        const int DefaultPort = 47863;
        const int MainThreadTimeoutMs = 10000;

        class Request
        {
            public string Tool;
            public Dictionary<string, object> Args;
            public object Result;
            public Exception Error;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }

        static readonly ConcurrentQueue<Request> queue = new ConcurrentQueue<Request>();
        static HttpListener listener;
        static Thread thread;
        static string token;
        static string endpointFile;
        static volatile bool running;

        public static void Start()
        {
            var port = DefaultPort;
            if (GenCommandLine.TryGetCommandLineArg("rimmcp-port", out var p) && int.TryParse(p, out var parsed))
                port = parsed;
            token = NewToken();
            try
            {
                listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                listener.Start();
            }
            catch (Exception e)
            {
                Log.Error($"[RimMcp] could not listen on 127.0.0.1:{port} ({e.Message}). " +
                          "Another RimWorld running? Start with -rimmcp-port=<n>.");
                return;
            }
            running = true;
            WriteEndpointFile(port);
            thread = new Thread(Listen) { IsBackground = true, Name = "RimMcp listener" };
            thread.Start();
            Log.Message($"[RimMcp] listening on 127.0.0.1:{port}");
        }

        public static void Stop()
        {
            running = false;
            try { listener?.Close(); } catch { }
            try { if (endpointFile != null) File.Delete(endpointFile); } catch { }
        }

        static string NewToken()
        {
            var bytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        static void WriteEndpointFile(int port)
        {
            var dir = Path.Combine(GenFilePaths.SaveDataFolderPath, "RimMcp");
            Directory.CreateDirectory(dir);
            endpointFile = Path.Combine(dir, "endpoint.json");
            File.WriteAllText(endpointFile, Json.Write(new Dictionary<string, object>
            {
                ["port"] = port,
                ["token"] = token,
                ["pid"] = Process.GetCurrentProcess().Id,
            }));
        }

        static void Listen()
        {
            while (running)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); }
                catch { if (!running) return; continue; }
                ThreadPool.QueueUserWorkItem(_ => Handle(ctx));
            }
        }

        static void Handle(HttpListenerContext ctx)
        {
            int status = 200;
            object body;
            try
            {
                if (ctx.Request.Headers["Authorization"] != "Bearer " + token)
                {
                    status = 401;
                    body = Error("bad or missing token");
                }
                else if (ctx.Request.HttpMethod == "GET" && ctx.Request.Url.AbsolutePath == "/health")
                {
                    body = new Dictionary<string, object> { ["ok"] = true, ["version"] = "1" };
                }
                else if (ctx.Request.HttpMethod == "GET" && ctx.Request.Url.AbsolutePath == "/wait")
                {
                    // block on this listener thread (not the main thread) until the game pauses or
                    // the time is up: a run costs one round trip instead of one status poll per second
                    double secs = 60;
                    double.TryParse(ctx.Request.QueryString["seconds"], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out secs);
                    var sw = Stopwatch.StartNew();
                    bool paused = false;
                    while (sw.Elapsed.TotalSeconds < Math.Min(Math.Max(secs, 0.5), 600))
                    {
                        if (IsPaused()) { paused = true; break; }
                        Thread.Sleep(250);
                    }
                    body = new Dictionary<string, object> { ["ok"] = true, ["result"] = new Dictionary<string, object> { ["paused"] = paused, ["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 1) } };
                }
                else if (ctx.Request.HttpMethod == "POST" && ctx.Request.Url.AbsolutePath == "/call")
                {
                    string text;
                    using (var r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) text = r.ReadToEnd();
                    var req = (Dictionary<string, object>)Json.Parse(text);
                    body = RunOnMainThread(req["tool"] as string,
                        req.TryGetValue("args", out var a) && a is Dictionary<string, object> d ? d : new Dictionary<string, object>());
                }
                else
                {
                    status = 404;
                    body = Error("unknown route");
                }
            }
            catch (Exception e)
            {
                status = 500;
                body = Error(e.Message);
            }
            try
            {
                var bytes = Encoding.UTF8.GetBytes(Json.Write(body));
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.Close();
            }
            catch { }
        }

        // read from the listener thread; a stale value only costs one more 250 ms loop
        static bool IsPaused()
        {
            try
            {
                var tm = Current.Game?.tickManager;
                return tm == null || tm.Paused || Current.ProgramState != ProgramState.Playing;
            }
            catch { return false; }
        }

        static object RunOnMainThread(string tool, Dictionary<string, object> args)
        {
            var req = new Request { Tool = tool, Args = args };
            queue.Enqueue(req);
            if (!req.Done.Wait(MainThreadTimeoutMs))
                return Error("game did not answer in time (loading or frozen?)");
            if (req.Error != null)
                return Error(req.Error is ToolError ? req.Error.Message : req.Error.GetType().Name + ": " + req.Error.Message);
            return new Dictionary<string, object> { ["ok"] = true, ["result"] = req.Result };
        }

        public static void DrainOnMainThread()
        {
            while (queue.TryDequeue(out var req))
            {
                try { req.Result = Tools.Run(req.Tool, req.Args); }
                catch (Exception e) { req.Error = e; }
                req.Done.Set();
            }
        }

        static Dictionary<string, object> Error(string message) =>
            new Dictionary<string, object> { ["ok"] = false, ["error"] = message };
    }

    public class ToolError : Exception
    {
        public ToolError(string message) : base(message) { }
    }
}
