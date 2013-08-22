﻿using System;
using System.Configuration;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime;
using System.Text;
using System.Threading;
using System.Windows;
using Livet;
using StarryEyes.Anomaly.TwitterApi;
using StarryEyes.Helpers;
using StarryEyes.Models;
using StarryEyes.Models.Plugins;
using StarryEyes.Models.Receivers;
using StarryEyes.Models.Stores;
using StarryEyes.Models.Subsystems;
using StarryEyes.Nightmare.Windows;
using StarryEyes.Settings;
using StarryEyes.Vanille.DataStore.Persistent;

namespace StarryEyes
{
    /// <summary>
    /// App.xaml の相互作用ロジック
    /// </summary>
    public partial class App
    {
        private static Mutex _appMutex;
        private void Application_Startup(object sender, StartupEventArgs e)
        {
            #region initialize execution environment

            // enable multi-core JIT.
            // see reference: http://msdn.microsoft.com/en-us/library/system.runtime.profileoptimization.aspx
            if (IsMulticoreJitEnabled)
            {
                ProfileOptimization.SetProfileRoot(ConfigurationDirectoryPath);
                ProfileOptimization.StartProfile(ProfileFileName);
            }

            // initialize dispatcher helper
            DispatcherHelper.UIDispatcher = Dispatcher;
            DispatcherHolder.Initialize(Dispatcher);

            // set rendering mode
            if (!IsHardwareRenderingEnabled)
            {
                System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            }

            #endregion

            #region detect run duplication

            // Check run duplication
            string mutexStr = null;
            switch (ExecutionMode)
            {
                case ExecutionMode.Default:
                case ExecutionMode.Roaming:
                    mutexStr = ExecutionMode.ToString();
                    break;
                case ExecutionMode.Standalone:
                    mutexStr = "Standalone_" + ExeFilePath.Replace('\\', '*');
                    break;
            }
            _appMutex = new Mutex(true, "Krile_StarryEyes_" + mutexStr);

            if (_appMutex.WaitOne(0, false) == false)
            {
                TaskDialog.Show(new TaskDialogOptions
                {
                    MainIcon = VistaTaskDialogIcon.Error,
                    MainInstruction = "Krileはすでに起動しています。",
                    Content = "同じ設定を共有するKrileを多重起動することはできません。",
                    Title = "Krile",
                    ExpandedInfo = "Krileを多重起動するためには、krile.exe.configを編集する必要があります。" + Environment.NewLine +
                    "詳しくは公式ウェブサイト上のFAQを参照してください。",
                    CommonButtons = TaskDialogCommonButtons.Close
                });
                Environment.Exit(0);
            }

            #endregion

            #region register core handlers

            // set exception handlers
            Current.DispatcherUnhandledException += (sender2, e2) => HandleException(e2.Exception);
            AppDomain.CurrentDomain.UnhandledException += (sender2, e2) => HandleException(e2.ExceptionObject as Exception);

            // set exit handler
            Current.Exit += (_, __) => AppFinalize(true);

            #endregion

            #region initialize web connection parameters

            // initialize service points
            ServicePointManager.Expect100Continue = false; // disable expect 100 continue for User Streams connection.
            ServicePointManager.DefaultConnectionLimit = Int32.MaxValue; // Limit Break!

            // declare security protocol explicitly
            // for Windows 8.1 (Preview)
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls | SecurityProtocolType.Ssl3;

            // initialize anomaly core system
            Anomaly.Core.Initialize();

            #endregion

            // initialize special image handlers
            SpecialImageResolvers.Initialize();

            // load plugins
            PluginManager.Load();

            // load settings
            if (!Setting.LoadSettings())
            {
                // fail loading settings
                Current.Shutdown();
                return;
            }

            // set parameters for accessing twitter.
            ApiEndpoint.UserAgent = Setting.UserAgent.Value;
            ApiEndpoint.ApiEndpointProxy = Setting.ApiProxy.Value;

            // web access proxy
            Networking.Initialize();

            if (!this.CheckDatabase())
            {
                // database corrupted
                Current.Shutdown();
                return;
            }

            // load key assigns
            KeyAssignManager.Initialize();

            // load cache manager
            CacheStore.Initialize();

            // initialize stores
            StatusStore.Initialize();
            UserStore.Initialize();

            // initialize subsystems
            StatisticsService.Initialize();
            PostLimitPredictionService.Initialize();
            TwitterEventService.Initialize();
            ReceiveInbox.Initialize();

            // activate plugins
            PluginManager.LoadedPlugins.ForEach(p => p.Initialize());

            // activate scripts
            ScriptingManager.ExecuteScripts();

            // finalize handlers
            TwitterEventService.RegisterDefaultHandlers();
            ReceiversManager.Initialize();
            TwitterConfigurationService.Initialize();
            BackstageModel.Initialize();
            RaiseSystemReady();
        }

        private bool CheckDatabase()
        {
            // check database corruption
            if (!Setting.DatabaseCorruption.Value)
            {
                return true;
            }
            var option = new TaskDialogOptions
            {
                MainIcon = VistaTaskDialogIcon.Error,
                Title = "Krile データベースの初期化Krile データストア初期化エラー",
                MainInstruction = "データストアの破損が検出されています。",
                Content = "データストアが破損している可能性があります。" + Environment.NewLine +
                          "データストアを初期化するか、またはKrileを終了してバックアップを取ることができます。",
                CommandButtons = new[] { "データストアを初期化して再起動", "Krileを終了", "無視して起動を続ける" }
            };
            var result = TaskDialog.Show(option);
            switch (result.CommandButtonResult ?? -1)
            {
                case 0:
                    StatusStore.Shutdown();
                    UserStore.Shutdown();
                    // clear data
                    this.ClearStoreData();
                    Setting.DatabaseCorruption.Value = false;
                    Setting.Save();
                    _appMutex.ReleaseMutex();
                    _appMutex.Dispose();
                    Process.Start(ResourceAssembly.Location);
                    Current.Shutdown();
                    Process.GetCurrentProcess().Kill();
                    return false;
                case 1:
                    _appMutex.ReleaseMutex();
                    _appMutex.Dispose();
                    // shutdown
                    Current.Shutdown();
                    Process.GetCurrentProcess().Kill();
                    return false;
                case 2:
                    Setting.DatabaseCorruption.Value = false;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// アプリケーションのファイナライズ
        /// </summary>
        /// <param name="shutdown">アプリケーションが正しく終了している状態か</param>
        void AppFinalize(bool shutdown)
        {
            if (shutdown)
            {
                Debug.WriteLine("App Normal Exit");
                // 正規の終了
                RaiseApplicationExit();
            }
            Debug.WriteLine("App Finalize");
            RaiseApplicationFinalize();
            try
            {
                _appMutex.ReleaseMutex();
                _appMutex.Dispose();
            }
            catch (ObjectDisposedException)
            { }
        }

        private static int _observedDpxCount;
        private void HandleException(Exception ex)
        {
            try
            {
                var dex = ex as DataPersistenceException;
                if (dex != null)
                {
                    if (Interlocked.Increment(ref _observedDpxCount) != 1) return;
                    Setting.DatabaseCorruption.Value = true;
                    Setting.Save();
                    _appMutex.ReleaseMutex();
                    _appMutex.Dispose();
                    Process.Start(ResourceAssembly.Location);
                    Current.Shutdown();
                    return;
                }
                // TODO:ロギング処理など
                Debug.WriteLine("##### SYSTEM CRASH! #####");
                Debug.WriteLine(ex.ToString());

                // Build stack trace report file
                var builder = new StringBuilder();
                builder.AppendLine("Krile STARRYEYES #" + FormattedVersion);
                builder.AppendLine(Environment.OSVersion + " " + (Environment.Is64BitProcess ? "x64" : "x86"));
                builder.AppendLine("execution mode: " + ExecutionMode.ToString() + " " +
                    "multicore JIT: " + IsMulticoreJitEnabled.ToString() + ", " +
                    "hardware rendering: " + IsHardwareRenderingEnabled.ToString());
                builder.AppendLine();
                builder.AppendLine("thrown:");
                builder.AppendLine(ex.ToString());
#if DEBUG
                var tpath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    "StarryEyes_Dump_" + Path.GetRandomFileName() + ".txt");
                using (var sw = new StreamWriter(tpath))
                {
                    sw.WriteLine(builder.ToString());
                }
#else
                var tpath = Path.GetTempFileName();
                using (var sw = new StreamWriter(tpath))
                {
                    sw.WriteLine(builder.ToString());
                }
                var apppath = Path.GetDirectoryName(Environment.GetCommandLineArgs()[0]);
                System.Diagnostics.Process.Start(Path.Combine(apppath, App.FeedbackAppName), tpath);
#endif
            }
            finally
            {
                // アプリケーション ファイナライズ
                AppFinalize(false);
            }

            Environment.Exit(-1);
        }

        /// <summary>
        /// Clear all data stored in data store.
        /// </summary>
        internal void ClearStoreData()
        {
            if (Directory.Exists(DataStorePath))
            {
                Directory.Delete(DataStorePath, true);
            }
        }

        #region Definitions

        public const string AppShortName = "Krile";

        public const string AppFullName = "Krile STARRYEYES";

        internal static readonly string ConsumerKey = "HzbATXmr3JpNXRPDNtkXww";

        internal static readonly string ConsumerSecret = "BfBH1h68S45X4kAlVdJAoopbEIEyF43kaRQe1vC2po";

        public static bool IsOperatingSystemSupported
        {
            get
            {
                return Environment.OSVersion.Version.Major == 6;
            }
        }

        public static string ExeFilePath
        {
            get
            {
                return Process.GetCurrentProcess().MainModule.FileName;
            }
        }

        public static ExecutionMode ExecutionMode
        {
            get
            {
                switch (ConfigurationManager.AppSettings["ExecutionMode"].ToLower())
                {
                    case "standalone":
                    case "portable":
                        return ExecutionMode.Standalone;

                    case "roaming":
                        return ExecutionMode.Roaming;

                    default:
                        return ExecutionMode.Default;
                }
            }
        }

        public static bool IsMulticoreJitEnabled
        {
            get
            {
                if (ConfigurationManager.AppSettings["UseMulticoreJIT"].ToLower() == "none")
                    return false;
                return true;
            }
        }

        public static bool IsHardwareRenderingEnabled
        {
            get
            {
                if (ConfigurationManager.AppSettings["UseHardwareRendering"].ToLower() == "none")
                    return false;
                return true;
            }
        }

        public static string ConfigurationDirectoryPath
        {
            get
            {
                switch (ExecutionMode)
                {
                    case ExecutionMode.Standalone:
                        return Path.GetDirectoryName(ExeFilePath);
                    case ExecutionMode.Roaming:
                        return Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            "Krile");
                    default:
                        // setting hold in "Local"
                        return Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "Krile");
                }
            }
        }

        public static string ConfigurationFilePath
        {
            get
            {
                return Path.Combine(ConfigurationDirectoryPath, ConfigurationFileName);
            }
        }

        public static string DataStorePath
        {
            get
            {
                return Path.Combine(ConfigurationDirectoryPath, DataStoreDirectory);
            }
        }

        public static string HashtagTempFilePath
        {
            get { return Path.Combine(ConfigurationDirectoryPath, HashtagCacheFileName); }
        }

        public static string ListUserTempFilePath
        {
            get { return Path.Combine(ConfigurationDirectoryPath, ListCacheFileName); }
        }

        private static FileVersionInfo _version;
        public static FileVersionInfo Version
        {
            get
            {
                return _version ??
                    (_version = FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location));
            }
        }

        public static string FormattedVersion
        {
            get
            {
                var basestr = Version.FileMajorPart + "." +
                              Version.FileMinorPart + "." +
                              Version.FilePrivatePart;
                switch (ReleaseKind)
                {
                    case ReleaseKind.Stable:
                        return basestr;
                    case ReleaseKind.Daybreak:
                        return basestr + " daybreak";
                    case ReleaseKind.Midnight:
                        return basestr + " midnight #" + Version.FileBuildPart;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
        }

        public static bool IsUnstableVersion
        {
            get { return Version.FilePrivatePart % 2 == 1; }
        }

        public static ReleaseKind ReleaseKind
        {
            get
            {
                // A.B.C.D
                // C % 2 == 0 -> Stable
                // C % 2 == 1 && D % 2 == 0 -> Daybreak
                // C % 2 == 1 && D % 2 == 1 -> Midnight
                return Version.FilePrivatePart % 2 == 0
                           ? ReleaseKind.Stable
                           : (Version.FileBuildPart % 2 == 0
                                  ? ReleaseKind.Daybreak
                                  : ReleaseKind.Midnight);
            }
        }

        public static double NumericVersion
        {
            get
            {
                var lvstr = (Version.FileMajorPart * 1000 + Version.FileMinorPart).ToString(CultureInfo.InvariantCulture) + "." +
                    Version.FilePrivatePart.ToString(CultureInfo.InvariantCulture);
                return Double.Parse(lvstr);
            }
        }

        public static readonly string DataStoreDirectory = "store";

        public static readonly string KeyAssignProfilesDirectory = "assigns";

        public static readonly string MediaDirectory = "media";

        public static readonly string PluginDirectory = "plugins";

        public static readonly string PluginSignatureFile = "auth.sig";

        public static readonly string ScriptDirectiory = "scripts";

        public static readonly string ConfigurationFileName = "krile.xml";

        public static readonly string HashtagCacheFileName = "tags.cache";

        public static readonly string ListCacheFileName = "lists.cache";

        public static readonly string ProfileFileName = "krile.profile";

        public static readonly string FeedbackAppName = "reporter.exe";

        public static readonly string UpdateFileName = "kup.exe";

        public static readonly string DefaultStatusMessage = "完了";

        public static readonly string RemoteVersionXml = "http://update.starwing.net/starryeyes/update.xml";

        public static readonly string PublicKeyFile = "krile.pub";

        public static readonly string MentionWavFile = "mention.wav";

        public static readonly string NewReceiveWavFile = "new.wav";

        public static readonly string DirectMessageWavFile = "directmessage.wav";

        public static readonly string EventWavFile = "event.wav";

        public static readonly string ReleaseNoteUrl = "http://krile.starwing.net/updates.html";

        public static readonly string KampaUrl = "http://krile.starwing.net/kampa.html";

        #endregion

        #region Triggers

        /// <summary>
        /// Call on kernel systems are ready<para />
        /// (But UI is not prepared)
        /// </summary>
        public static event Action SystemReady;
        internal static void RaiseSystemReady()
        {
            Debug.WriteLine("# System ready.");
            var osr = SystemReady;
            SystemReady = null;
            if (osr != null)
                osr();
        }

        /// <summary>
        /// Call on user interfaces are ready
        /// </summary>
        public static event Action UserInterfaceReady;
        internal static void RaiseUserInterfaceReady()
        {
            DebugHelper.EnsureBackgroundThread();
            Debug.WriteLine("# UI ready.");
            var usr = UserInterfaceReady;
            UserInterfaceReady = null;
            if (usr != null)
                usr();
        }

        /// <summary>
        /// Call on aplication is exit from user action<para />
        /// (On crash app, this handler won't call!)
        /// </summary>
        public static event Action ApplicationExit;
        internal static void RaiseApplicationExit()
        {
            Debug.WriteLine("# App exit.");
            var apx = ApplicationExit;
            ApplicationExit = null;
            if (apx != null)
                apx();
        }

        /// <summary>
        /// Call on application is exit from user action or crashed
        /// </summary>
        public static event Action ApplicationFinalize;
        internal static void RaiseApplicationFinalize()
        {
            Debug.WriteLine("# App finalize.");
            var apf = ApplicationFinalize;
            ApplicationFinalize = null;
            if (apf != null)
                apf();
        }

        #endregion

        public static readonly DateTime StartupDateTime = DateTime.Now;
    }

    public enum ExecutionMode
    {
        Default,
        Roaming,
        Standalone,
    }

    public enum ReleaseKind
    {
        Stable,
        Daybreak,
        Midnight,
    }
}
