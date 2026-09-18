using System;
using System.Windows.Forms;

namespace DSAnimStudio
{

    /// <summary>
    /// The main class.
    /// </summary>
    public static class Program
    {
        public static string[] ARGS;
        public static Main MainInstance;

        // [Preview] 全局崩溃日志 —— 把任何未捕获异常写到 exe 同目录的 dsas_crash.log
        // 这样 c0000.anibnd 闪退时不再静默退出，能拿到完整堆栈。
        static string CrashLogPath
        {
            get
            {
                try { return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dsas_crash.log"); }
                catch { return "dsas_crash.log"; }
            }
        }

        static void WriteCrashLog(string source, Exception ex)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("==================================================================");
                sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] DSAS Crash via {source}");
                sb.AppendLine("==================================================================");
                if (ex != null)
                {
                    int depth = 0;
                    var cur = ex;
                    while (cur != null && depth < 10)
                    {
                        sb.AppendLine($"-- Layer {depth}: {cur.GetType().FullName}");
                        sb.AppendLine($"   Message: {cur.Message}");
                        if (!string.IsNullOrEmpty(cur.StackTrace))
                        {
                            sb.AppendLine("   StackTrace:");
                            sb.AppendLine(cur.StackTrace);
                        }
                        cur = cur.InnerException;
                        depth++;
                    }
                }
                else
                {
                    sb.AppendLine("(null exception)");
                }
                sb.AppendLine();
                System.IO.File.AppendAllText(CrashLogPath, sb.ToString());
            }
            catch { /* never let the logger itself crash */ }
        }

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            // [Preview] 注册全局兜底
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                WriteCrashLog("AppDomain.UnhandledException", e.ExceptionObject as Exception);
            };
            Application.ThreadException += (s, e) =>
            {
                WriteCrashLog("Application.ThreadException", e.Exception);
            };
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                WriteCrashLog("TaskScheduler.UnobservedTaskException", e.Exception);
                e.SetObserved();
            };


            //SoulsFormatsNEXT TODO
            //SoulsFormats.DCX.LoadOodleAction = () =>
            //{
            //    MessageBox.Show("To load Sekiro / Elden Ring / Armored Core 6 / Nightreign files, you need to give DS Anim Studio access to the 'oo2core_6_win64.dll', 'oo2core_8_win64.dll', or 'oo2core_9_win64.dll' file bundled next to the EXE of either game. Click OK to browse to this file now.");

            //    var browseDlg = new OpenFileDialog()
            //    {
            //        FileName = "",
            //        CheckFileExists = false,
            //        CheckPathExists = true,
            //        Title = "Select Oodle DLL",
            //        Filter = "DLLs (*.dll)|*.dll"
            //    };
            //    browseDlg.FileName = "oo2core_6_win64.dll";
            //    if (browseDlg.ShowDialog() == DialogResult.OK)
            //    {
            //        System.IO.File.Copy(browseDlg.FileName, DSAnimStudio.Main.Directory + "\\oo2core_6_win64.dll", true);
            //    }
            //};

            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            

            ARGS = args;
            //ARGS = new string[] { @"C:\Program Files (x86)\Steam\steamapps\common\Dark Souls Prepare to Die Edition\DATA\chr\c4100_bak-chrbnd\chr\c4100\c4100.flver" };

#if !DEBUG
            try
            {
#endif
                MainInstance = new Main();

#if !DEBUG
        }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Error occurred before DS Anim Studio had a chance to initialize (please report):\n\n{ex.ToString()}",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
#endif

            try
            {
                MainInstance.Run(Microsoft.Xna.Framework.GameRunBehavior.Synchronous);
            }
            // [Preview] 捕获 Run() 内的所有异常并写日志 + 弹对话框
            catch (Exception ex)
            {
                WriteCrashLog("MainInstance.Run", ex);
                try
                {
                    MessageBox.Show(
                        $"DSAS crashed during runtime.\n\nA crash log has been written to:\n{CrashLogPath}\n\n" +
                        $"Top-level exception: {ex.GetType().Name}\n{ex.Message}",
                        "DSAS Crash",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch { /* swallow */ }
            }
            finally
            {
                LiveRefresh.Memory.CloseHandle();
                MainInstance?.Dispose();
                System.Diagnostics.Process.GetCurrentProcess().Kill();
            }
        }
    }

}
