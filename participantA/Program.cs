using System.Windows.Forms;

var crashLog = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "participant-crash.log");
void LogCrash(string source, object payload)
{
    try { System.IO.File.AppendAllText(crashLog, $"[{DateTime.Now:o}] {source}: {payload}\n\n"); } catch { }
}

Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
Application.ThreadException += (_, e) =>
{
    LogCrash("ThreadException", e.Exception);
    MessageBox.Show(e.Exception.ToString(), "Unhandled UI exception (logged)", MessageBoxButtons.OK, MessageBoxIcon.Error);
};
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    LogCrash("AppDomain.UnhandledException", e.ExceptionObject ?? "unknown");
    try { MessageBox.Show(e.ExceptionObject?.ToString() ?? "unknown", "Unhandled domain exception (logged)", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
};
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    LogCrash("UnobservedTaskException", e.Exception);
    MessageBox.Show(e.Exception.ToString(), "Unobserved task exception (logged)", MessageBoxButtons.OK, MessageBoxIcon.Error);
    e.SetObserved();
};

ApplicationConfiguration.Initialize();
Application.Run(ParticipantForm.ForProvider());
