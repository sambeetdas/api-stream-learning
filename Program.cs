using System.Diagnostics;
using SseApi.Services;

// Debug helper: `run.ps1 -Mode Debug -WaitForDebugger` sets this so startup code can be debugged too.
if (Environment.GetEnvironmentVariable("WAIT_FOR_DEBUGGER") == "1")
{
    Console.WriteLine($"Waiting for a debugger to attach to PID {Environment.ProcessId}...");
    while (!Debugger.IsAttached)
        Thread.Sleep(200);
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddSingleton<MessageStore>();
builder.Services.AddSingleton<Broadcaster>();
builder.Services.AddSingleton<MessageFeed>();
builder.Services.AddSingleton<TransportStats>();

var app = builder.Build();

app.Services.GetRequiredService<MessageStore>().Init();

app.UseWebSockets();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();

app.Run();
