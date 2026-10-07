// NeutrinoOS SDK sample - TasksWebApp: a Web API project shape for NeutrinoOS.
//
// This is the "WebAPI project" version of the task-tracker REST API: the
// same endpoints as src/ddk/Services/SampleApi.cs, but written in the
// WebApplication / MapGet style that the Kestrel port targets
// (see docs/KESTREL-PORT.md). Compare the two files:
//
//   SampleApi.cs   - hand-wired: socket loop, parsing, routing, responses.
//   TasksWebApp.cs - declarative: MapGet/MapPost/MapPut/MapDelete handlers
//                    + ctx.Request / ctx.Response (this file).
//
// The kernel's ServiceRegistry JIT-compiles this class from the DDK
// assembly when `webapi start` runs, calls Start() once and Tick() from
// the shell idle hook - the WebApplication host does the rest. The task
// store is deliberately tiny (8 slots, in memory).
//
// Endpoints (identical to the rest-api sample; see docs/samples/webapi):
//   GET    /                      service blurb + endpoint index
//   GET    /api/v1/info           uptime, request count, task count
//   GET    /api/v1/echo?msg=...   query-string demo
//   POST   /api/v1/echo           request-body demo (echoes the bytes)
//   GET    /api/v1/tasks          list tasks
//   POST   /api/v1/tasks          create (JSON {"name":"..."} or form name=...)
//   GET    /api/v1/tasks/{id}     read one task    (404 when missing)
//   PUT    /api/v1/tasks/{id}     rename one task  (404 when missing)
//   DELETE /api/v1/tasks/{id}     delete one task  (404 when missing)
//
// Run from the NeutrinoOS shell:   webapi start   (stop | status)
// Reach it from Windows 11: boot QEMU with
//   -netdev user,id=n0,hostfwd=tcp::18080-:8080
// then use http://127.0.0.1:18080 . Postman collection:
// docs/samples/webapi/NeutrinoWebApi.postman_collection.json

using System;
using NeutrinoOS.DDK.Kernel;
using NeutrinoOS.DDK.Services;   // WebService string helpers
using NeutrinoOS.DDK.Web;

namespace NeutrinoOS.DDK.Samples;

/// <summary>The WebAPI-style sample app (a cooperative service).</summary>
public static class TasksWebApp
{
    private const int MaxTasks = 8;
    private const int MaxTaskNameLength = 64;

    private static WebApplication _app;
    private static ulong _startedAt;

    // In-memory task store: null = free slot, id = slot + 1.
    private static readonly string[] _taskNames = new string[MaxTasks];
    private static int _taskCount;

    /// <summary>True while the app's listener is running.</summary>
    public static bool Active => _app != null && _app.IsRunning;

    // ====================================================================
    // Service contract (called by the kernel ServiceRegistry)
    // ====================================================================

    /// <summary>Start the app; 0 = ok (see WebApplication.Start).</summary>
    public static int Start()
    {
        if (_app != null && _app.IsRunning)
            return 0;

        if (_app == null)
            _app = Build();

        for (int i = 0; i < _taskNames.Length; i++)
            _taskNames[i] = null;
        _taskCount = 0;
        _startedAt = Timer.GetUptimeMilliseconds();

        return _app.Start();
    }

    /// <summary>One bounded work slice (kernel idle hook).</summary>
    public static void Tick()
    {
        if (_app != null)
            _app.Tick();
    }

    /// <summary>Stop the app.</summary>
    public static void Stop()
    {
        if (_app != null)
            _app.Stop();
    }

    // ====================================================================
    // The Web API definition (this is the part you rewrite for your app)
    // ====================================================================

    /// <summary>Wire up the routes - the equivalent of an ASP.NET Core Program.</summary>
    private static WebApplication Build()
    {
        var app = WebApplication.CreateBuilder(new string[0]);
        app.Name = "webapi";
        app.Port = 8080;

        app.MapGet("/", OnRoot);
        app.MapGet("/api/v1/info", OnInfo);
        app.MapGet("/api/v1/echo", OnEchoGet);
        app.MapPost("/api/v1/echo", OnEchoPost);
        app.MapGet("/api/v1/tasks", OnListTasks);
        app.MapPost("/api/v1/tasks", OnCreateTask);
        app.MapGet("/api/v1/tasks/{id}", OnGetTask);
        app.MapPut("/api/v1/tasks/{id}", OnRenameTask);
        app.MapDelete("/api/v1/tasks/{id}", OnDeleteTask);

        return app;
    }

    // ---- handlers ------------------------------------------------------

    private static void OnRoot(HttpContext ctx)
    {
        ctx.Response.Json(
            "{\"service\":\"NeutrinoOS WebAPI sample\",\"port\":" + WebService.IntToStr(_app.Port) +
            ",\"style\":\"WebApplication minimal API (Kestrel-port M1)\"," +
            "\"endpoints\":[\"GET /api/v1/info\",\"GET /api/v1/echo?msg=...\"," +
            "\"POST /api/v1/echo\",\"GET /api/v1/tasks\",\"POST /api/v1/tasks\"," +
            "\"GET /api/v1/tasks/{id}\",\"PUT /api/v1/tasks/{id}\"," +
            "\"DELETE /api/v1/tasks/{id}\"]}\n");
    }

    private static void OnInfo(HttpContext ctx)
    {
        int uptime = (int)((Timer.GetUptimeMilliseconds() - _startedAt) / 1000);
        ctx.Response.Json(
            "{\"service\":\"webapi\",\"version\":\"1.0.0\",\"uptime_s\":" +
            WebService.IntToStr(uptime) +
            ",\"requests\":" + WebService.IntToStr(_app.RequestCount) +
            ",\"tasks\":" + WebService.IntToStr(_taskCount) + "}\n");
    }

    private static void OnEchoGet(HttpContext ctx)
    {
        string msg = ctx.Request.Query("msg");
        ctx.Response.Json("{\"method\":\"GET\",\"msg\":\"" + JsonEscape(msg ?? "") + "\"}\n");
    }

    private static void OnEchoPost(HttpContext ctx)
    {
        string body = ctx.Request.Body;
        ctx.Response.Json(
            "{\"method\":\"POST\",\"content_length\":" + WebService.IntToStr(body.Length) +
            ",\"you_sent\":\"" + JsonEscape(body) + "\"}\n");
    }

    private static void OnListTasks(HttpContext ctx)
    {
        ctx.Response.Json(TasksJson());
    }

    private static void OnCreateTask(HttpContext ctx)
    {
        string name = ExtractTaskName(ctx.Request.Body);
        if (name == null || name.Length == 0)
        {
            ctx.Response.Status(400);
            ctx.Response.Json("{\"error\":\"name required\"}\n");
            return;
        }
        if (name.Length > MaxTaskNameLength)
            name = name.Substring(0, MaxTaskNameLength);
        int slot = -1;
        for (int i = 0; i < MaxTasks; i++)
        {
            if (_taskNames[i] == null)
            {
                slot = i;
                break;
            }
        }
        if (slot < 0)
        {
            ctx.Response.Status(409);
            ctx.Response.Json("{\"error\":\"task store full\"}\n");
            return;
        }
        _taskNames[slot] = name;
        _taskCount++;
        ctx.Response.Status(201);
        ctx.Response.Json(TaskJson(slot) + "\n");
    }

    private static void OnGetTask(HttpContext ctx)
    {
        int id = RouteTaskId(ctx);
        if (id < 0)
        {
            NotFound(ctx);
            return;
        }
        if (_taskNames[id - 1] == null)
        {
            NotFound(ctx);
            return;
        }
        ctx.Response.Json(TaskJson(id - 1) + "\n");
    }

    private static void OnRenameTask(HttpContext ctx)
    {
        int id = RouteTaskId(ctx);
        if (id < 0 || _taskNames[id - 1] == null)
        {
            NotFound(ctx);
            return;
        }
        string name = ExtractTaskName(ctx.Request.Body);
        if (name == null || name.Length == 0)
        {
            ctx.Response.Status(400);
            ctx.Response.Json("{\"error\":\"name required\"}\n");
            return;
        }
        if (name.Length > MaxTaskNameLength)
            name = name.Substring(0, MaxTaskNameLength);
        _taskNames[id - 1] = name;
        ctx.Response.Json(TaskJson(id - 1) + "\n");
    }

    private static void OnDeleteTask(HttpContext ctx)
    {
        int id = RouteTaskId(ctx);
        if (id < 0 || _taskNames[id - 1] == null)
        {
            NotFound(ctx);
            return;
        }
        _taskNames[id - 1] = null;
        _taskCount--;
        ctx.Response.Json("{\"deleted\":" + WebService.IntToStr(id) + "}\n");
    }

    private static void NotFound(HttpContext ctx)
    {
        ctx.Response.Status(404);
        ctx.Response.Json("{\"error\":\"not found\"}\n");
    }

    // ====================================================================
    // Helpers (the small "model binding" layer a real framework provides)
    // ====================================================================

    /// <summary>The {id} route value as 1..8, or -1 when absent/invalid.</summary>
    private static int RouteTaskId(HttpContext ctx)
    {
        string s = ctx.Request.Route("id");
        if (s == null || s.Length == 0)
            return -1;
        int value = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c < '0' || c > '9')
                return -1;
            value = value * 10 + (c - '0');
        }
        if (value < 1 || value > MaxTasks)
            return -1;
        return value;
    }

    /// <summary>Name from the body: JSON {"name":"..."} or form name=....</summary>
    private static string ExtractTaskName(string reqBody)
    {
        if (reqBody == null)
            return null;
        int key = IndexOfStr(reqBody, "\"name\"", 0);
        if (key >= 0)
        {
            int colon = WebService.IndexOfChar2(reqBody, ':', key + 6);
            if (colon >= 0)
            {
                int q1 = WebService.IndexOfChar2(reqBody, '"', colon + 1);
                int q2 = q1 >= 0 ? WebService.IndexOfChar2(reqBody, '"', q1 + 1) : -1;
                if (q2 > q1)
                    return reqBody.Substring(q1 + 1, q2 - q1 - 1);
            }
        }
        int eq = IndexOfStr(reqBody, "name=", 0);
        if (eq >= 0)
        {
            int start = eq + 5;
            int end = WebService.IndexOfChar2(reqBody, '&', start);
            if (end < 0)
                end = reqBody.Length;
            var chars = new char[end - start];
            for (int i = start; i < end; i++)
                chars[i - start] = reqBody[i] == '+' ? ' ' : reqBody[i];
            return new string(chars);
        }
        return null;
    }

    /// <summary>JSON for one task slot: {"id":N,"name":"..."}.</summary>
    private static string TaskJson(int slot)
    {
        return "{\"id\":" + WebService.IntToStr(slot + 1) +
               ",\"name\":\"" + JsonEscape(_taskNames[slot]) + "\"}";
    }

    /// <summary>JSON for the whole collection.</summary>
    private static string TasksJson()
    {
        string body = "{\"count\":" + WebService.IntToStr(_taskCount) + ",\"tasks\":[";
        bool first = true;
        for (int i = 0; i < MaxTasks; i++)
        {
            if (_taskNames[i] == null)
                continue;
            if (!first)
                body += ",";
            first = false;
            body += TaskJson(i);
        }
        return body + "]}\n";
    }

    /// <summary>Escape a string for embedding inside JSON quotes.</summary>
    private static string JsonEscape(string s)
    {
        if (s == null || s.Length == 0)
            return "";
        var chars = new char[s.Length * 2 + 4];
        int n = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"' || c == '\\')
            {
                chars[n++] = '\\';
                chars[n++] = c;
            }
            else if (c == '\n')
            {
                chars[n++] = '\\';
                chars[n++] = 'n';
            }
            else if (c == '\r')
            {
                chars[n++] = '\\';
                chars[n++] = 'r';
            }
            else if (c == '\t')
            {
                chars[n++] = '\\';
                chars[n++] = 't';
            }
            else if (c < 32)
            {
                // Drop other control characters (JSON requires escapes).
            }
            else
            {
                chars[n++] = c;
            }
        }
        return new string(chars, 0, n);
    }

    private static int IndexOfStr(string s, string needle, int start)
    {
        for (int i = start; i + needle.Length <= s.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (s[i + j] != needle[j])
                {
                    ok = false;
                    break;
                }
            }
            if (ok)
                return i;
        }
        return -1;
    }
}
