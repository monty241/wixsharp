﻿using Microsoft.Deployment.Samples.EmbeddedUI;
using Microsoft.Deployment.WindowsInstaller;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using WixSharp.CommonTasks;
using WixSharp.Forms;

using forms = System.Windows.Forms;

namespace WixSharp
{
    /// <summary>
    /// Interface of the main window implementation of the MSI external/embedded UI. This interface is designed to be
    /// used by Wix#/MSI runtime (e.g. ManagedUI). It is the interface that is directly bound to the
    /// <see cref="T:Microsoft.Deployment.WindowsInstaller.IEmbeddedUI"/> (e.g. <see cref="T:WixSharp.ManagedUI"/>).
    /// </summary>
    interface IUIContainer
    {
        /// <summary>
        /// Shows the modal window of the MSI UI. This method is called by the <see cref="T:Microsoft.Deployment.WindowsInstaller.IEmbeddedUI"/>
        /// when it is initialized at runtime.
        /// </summary>
        /// <param name="runtime">The MSI runtime.</param>
        /// <param name="ui">The MSI external/embedded UI.</param>
        void ShowModal(InstallerRuntime runtime, IManagedUI ui);

        /// <summary>
        /// Called when MSI execution is complete.
        /// </summary>
        void OnExecuteComplete();

        /// <summary>
        /// Called when MSI execute started.
        /// </summary>
        void OnExecuteStarted();

        /// <summary>
        ///  Processes information and progress messages sent to the user interface.
        /// <para> This method directly mapped to the
        /// <see cref="T:Microsoft.Deployment.WindowsInstaller.IEmbeddedUI.ProcessMessage"/>.</para>
        /// </summary>
        /// <param name="messageType">Type of the message.</param>
        /// <param name="messageRecord">The message record.</param>
        /// <param name="buttons">The buttons.</param>
        /// <param name="icon">The icon.</param>
        /// <param name="defaultButton">The default button.</param>
        /// <returns></returns>
        MessageResult ProcessMessage(InstallMessage messageType, Record messageRecord, MessageButtons buttons, MessageIcon icon, MessageDefaultButton defaultButton);
    }

    /// <summary>
    /// The main window WinForms implementation of the MSI external/embedded UI.
    /// </summary>
    public partial class UIShell : IUIContainer, IManagedUIShell
    {
        /// <summary>
        /// Gets the runtime context object. Typically this object is of the <see cref="T:WixSharp.MsiRuntime" /> type.
        /// </summary>
        /// <value>
        /// The runtime context.
        /// </value>
        public object RuntimeContext { get { return Runtime; } }

        /// <summary>
        /// Gets or sets the runtime context object. Typically this object is of the <see cref="T:WixSharp.MsiRuntime" /> type.
        /// </summary>
        /// <value>
        /// The runtime context.
        /// </value>
        internal InstallerRuntime Runtime { get; set; }

        /// <summary>
        /// Gets or sets the UI.
        /// </summary>
        /// <value>The UI.</value>
        internal IManagedUI UI { get; set; }

        /// <summary>
        /// Gets a value indicating whether the MSI session was interrupted (canceled) by user.
        /// </summary>
        /// <value>
        ///   <c>true</c> if it was user interrupted; otherwise, <c>false</c>.
        /// </value>
        public bool UserInterrupted { get; set; }

        /// <summary>
        /// Gets a value indicating whether MSI session ended with error.
        /// </summary>
        /// <value>
        ///   <c>true</c> if error was detected; otherwise, <c>false</c>.
        /// </value>
        public bool ErrorDetected { get; set; }

        /// <summary>
        /// Gets or sets the custom error description to be displayed in the ExitDialog
        /// in case of <see cref="P:WixSharp.IManagedUIShell.ErrorDetected" /> being set to <c>true</c>.
        /// </summary>
        /// <value>
        /// The custom error description.
        /// </value>
        public string CustomErrorDescription { get; set; }

        /// <summary>
        /// Gets the MSI installation errors.
        /// </summary>
        /// <value>
        /// The errors.
        /// </value>
        public List<string> Errors { get; private set; } = new List<string>();

        /// <summary>
        /// Starts the execution of the MSI installation.
        /// </summary>
        public void StartExecute()
        {
            // Debug.Assert(false);
            UACRevealer.Enter();
            started = true;
            if (!IsDemoMode)
            {
                Runtime.StartExecute();
            }
        }

        InstallProgressCounter progressCounter = new InstallProgressCounter(0.5);
        volatile bool started = false;
        volatile bool canceled = false;
        volatile bool finished = false;

        /// <summary>
        /// Gets the sequence of the UI dialogs specific for the current setup type (e.g. install vs. modify).
        /// </summary>
        /// <value>
        /// The dialogs.
        /// </value>
        public ManagedDialogs Dialogs { get; set; }

        /// <summary>
        /// Gets the current dialog of the UI sequence.
        /// </summary>
        /// <value>The current dialog.</value>
        public IManagedDialog CurrentDialog { get; set; }

        /// <summary>
        /// Creates an instance of the WinForm dialog that can host WPF content.
        /// This method is not to be used by users but WixSharp compiler only.
        /// </summary>
        /// <returns></returns>
        public IWpfDialogHost CreateDefaultWpfDialgHost()
        {
            // only one type in WixSharp.UI.WPF should implement tis interface. It is a WinForm based WPF host.
            var host = System.Reflection.Assembly.Load("WixSharp.UI.WPF")
                                                 .GetTypes()
                                                 .Single(t => t.Implements<IWpfDialogHost>());

            return (IWpfDialogHost)Activator.CreateInstance(host);
        }

        Form shellView;
        int currentViewIndex = -1;

        /// <summary>
        /// Gets or sets the current dialog by the dialog index.
        /// </summary>
        /// <value>The index of the current dialog.</value>
        public int CurrentDialogIndex
        {
            get { return currentViewIndex; }

            set
            {
                currentViewIndex = value;

                // Debug.Assert(false);

                try
                {
                    if (currentViewIndex >= 0 && currentViewIndex < Dialogs.Count)
                    {
                        shellView.ClearChildren();

                        Type viewType = Dialogs[currentViewIndex];

                        Form view;

                        if (viewType.Implements<IWpfDialog>())
                        {
                            IWpfDialog wpfContent = (IWpfDialog)Activator.CreateInstance(viewType);

                            IWpfDialogHost winFormHost = CreateDefaultWpfDialgHost();
                            winFormHost.SetDialogContent(wpfContent);

                            view = (Form)winFormHost;
                        }
                        else if (viewType.GetGenericTypeBaseName() == typeof(CustomDialogWith<object>).GetGenericTypeBaseName())
                        {
                            // composite dialog with the content panel sup[plied by the user and the rest being a standard dialog base
                            Type userContentType = viewType.GetGenericArguments().FirstOrDefault();

                            var dialogBase = Type.GetType("WixSharp.UI.WPF.CustomDialogBase");

                            if (dialogBase == null) // the assembly may not be loaded yet
                                dialogBase = System.Reflection.Assembly.Load("WixSharp.UI.WPF")
                                                                       .GetType("WixSharp.UI.WPF.CustomDialogBase");

                            object userContent = Activator.CreateInstance(userContentType);
                            IWpfDialog wpfContentPresenter = (IWpfDialog)Activator.CreateInstance(dialogBase);
                            wpfContentPresenter.Invoke("SetUserContent", userContent);

                            IWpfDialogHost winFormHost = CreateDefaultWpfDialgHost();
                            winFormHost.SetDialogContent(wpfContentPresenter);

                            view = (Form)winFormHost;
                        }
                        else
                        {
                            view = (Form)Activator.CreateInstance(viewType);
                        }

                        view.LocalizeWith(Runtime.Localize);
                        view.FormBorderStyle = forms.FormBorderStyle.None;
                        shellView.ControlBox = view.ControlBox;
                        view.TopLevel = false;
                        //view.Dock = DockStyle.Fill; //do not use Dock as it interferes with scaling
                        view.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
                        view.Size = shellView.ClientSize;
                        view.Location = new System.Drawing.Point(0, 0);

                        CurrentDialog = (IManagedDialog)view;
                        CurrentDialog.Shell = this;

                        view.Parent = shellView;
                        view.Visible = true;
                        shellView.Text = view.Text; // the title is already set in `SetDialogContent` for WPF but for winform dialogs
                                                    // it needs to be set now. Changes to the text of the view later are NOT reflected in the shell view.

                        if (shellView is ShellView)
                        {
                            (shellView as ShellView).CurrentDialog = CurrentDialog;
                            (shellView as ShellView).RaiseCurrentDialogChanged(CurrentDialog);
                        }

                        try
                        {
                            Runtime.Session["WIXSHARP_RUNTIME_DATA"] = Runtime.Data.Serialize();
                        }
                        catch { /*expected to fail on deferred actions stage*/}
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// Shows the modal window of the MSI UI. This method is called by the <see cref="T:Microsoft.Deployment.WindowsInstaller.IEmbeddedUI" />
        /// when it is initialized at runtime.
        /// </summary>
        /// <param name="runtime">The MSI runtime.</param>
        /// <param name="ui">The MSI external/embedded UI.</param>
        public void ShowModal(InstallerRuntime runtime, IManagedUI ui)
        {
            // Debug.Assert(false);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            UI = ui;
            Runtime = runtime;
            Tasks.UILocalize = text => text.LocalizeWith(runtime.Localize);

            UACRevealer.Enabled = !runtime.Session.Property("UAC_REVEALER_ENABLED").IsEmpty();
            UACRevealer.WarningText = runtime.Session.Property("UAC_WARNING");

            if (runtime.Session.IsInstalling())
                Dialogs = ui.InstallDialogs;
            else if (runtime.Session.IsRepairing())
                Dialogs = ui.ModifyDialogs;

            if (Dialogs.Any())
            {
                shellView = new ShellView { Shell = this };

                var scalingModeValue = runtime.Session.Property("UI_AUTOSCALEMODE");
                if (scalingModeValue.IsNotEmpty())
                {
                    (shellView as ShellView).AutoScaleMode = (AutoScaleMode)Enum.Parse(typeof(AutoScaleMode), scalingModeValue);
                }
            }

            try
            {
                int culture = runtime.Session.Property("ProductLanguage").ToInt();
                if (culture != 0)
                    Thread.CurrentThread.CurrentUICulture = new CultureInfo(culture);
            }
            catch { }

            ActionResult result = runtime.InvokeClientHandlers("UIInitialized", shellView as IShellView);
            runtime.Data.MergeReplace(runtime.Session["WIXSHARP_RUNTIME_DATA"]); //data may be changed in the client handler

            if (result != ActionResult.Failure)
            {
                if (shellView != null)
                {
                    shellView.Load += (s, e) =>
                    {
                        Runtime.Session["WIXSHARP_MANAGED_UI_HANDLE"] =
                        Runtime.Data["WIXSHARP_MANAGED_UI_HANDLE"] = shellView.Handle.ToString();
                        try
                        {
                            var data = Runtime.Session.GetResourceData("ui_shell_icon");
                            using (var stream = new System.IO.MemoryStream(data))
                                shellView.Icon = new Icon(stream);
                        }
                        catch { }

                        result = runtime.InvokeClientHandlers("UILoaded", (IShellView)shellView);
                        if (result != ActionResult.Success)
                        {
                            // aborting UI dialogs sequence from here is not possible as this event is
                            // simply called when Shell is loaded but not when dialogs are progressing in the sequence.
                            runtime.Session.Log("UILoaded returned " + result);
                        }
                        runtime.Data.MergeReplace(runtime.Session["WIXSHARP_RUNTIME_DATA"]); ; //data may be changed in the client handler
                    };

                    if (result == ActionResult.SkipRemainingActions)
                    {
                        result = ActionResult.Success;
                        if (Dialogs.Contains(typeof(UI.Forms.ProgressDialog)))
                            GoTo<UI.Forms.ProgressDialog>();
                        else
                            GoToLast();
                    }
                    else if (result == ActionResult.UserExit)
                        GoToLast();
                    else
                        GoNext();

                    shellView.ShowDialog();
                }
                else
                {
                    this.StartExecute();
                    while (!finished)
                        Thread.Sleep(1000);
                }
            }
            else
            {
                Runtime.Session.Log("UIInitialized returned " + result);
            }
        }

        /// <summary>
        /// Plays the specified dialogs in demo mode.
        /// </summary>
        /// <param name="dialogs">The dialogs.</param>
        public static void Play(ManagedDialogs dialogs)
        {
            new UIShell().DemoPlay(dialogs);
        }

        /// <summary>
        /// Plays the specified dialog in demo mode.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        public static void Play<T>() where T : IManagedDialog, IDialog
        {
            var dialogs = new ManagedDialogs();
            dialogs.Add<T>();
            new UIShell().DemoPlay(dialogs);
        }

        /// <summary>
        /// Plays the specified dialog in demo mode.
        /// </summary>
        /// <param name="dialog">The dialog.</param>
        public static void Play(Type dialog)
        {
            var dialogs = new ManagedDialogs();
            dialogs.Add(dialog);
            new UIShell().DemoPlay(dialogs);
        }

        /// <summary>
        /// Plays the specified dialog set.
        /// </summary>
        /// <param name="demoProperties">The demo properties.</param>
        /// <param name="dialogSet">The dialog set.</param>
        public static void Play(string demoProperties, params Type[] dialogSet)
        {
            var dialogs = new ManagedDialogs();
            dialogSet.ForEach(dialog => dialogs.Add(dialog));
            new UIShell().DemoPlay(dialogs, demoProperties);
        }

        void DemoPlay(ManagedDialogs dialogs, string demoProperties = "")
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            IsDemoMode = true;

            var resourcesMsi = BuildUiPlayerResources();
            var dummySession = Installer.OpenPackage(resourcesMsi, true);

            foreach (var pair in demoProperties.ToDictionary())
                dummySession[pair.Key] = pair.Value;

            Runtime = new MsiRuntime(dummySession);
            Dialogs = dialogs;

            shellView = new ShellView();
            GoNext();
            shellView.ShowDialog();
        }

        static string BuildUiPlayerResources()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"WixSharp\Demo_UIPlayer");
            var result = Path.Combine(dir, "Demo_UIPlayer.msi");
            if (System.IO.File.Exists(result))
                return result;
            else
            {
                try { Compiler.OutputWriteLine("Building UIPlayer resources..."); } catch { }
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var project = new Project("Demo_UIPlayer",
                                  new Dir(@"%ProgramFiles%\WixSharp\Demo_UIPlayer"));

                project.OutDir = dir;

                string licence = ManagedUI.Default.LicenceFileFor(project);
                string localization = ManagedUI.Default.LocalizationFileFor(project);
                string bitmap = ManagedUI.Default.DialogBitmapFileFor(project);
                string banner = ManagedUI.Default.DialogBannerFileFor(project);

                project.AddBinaries(new Binary(new Id("WixSharp_UIText"), localization),
                                    new Binary(new Id("WixSharp_LicenceFile"), licence),
                                    new Binary(new Id("WixUI_Bmp_Dialog"), bitmap),
                                    new Binary(new Id("WixUI_Bmp_Banner"), banner));

                project.UI = WUI.WixUI_ProgressOnly;
                return project.BuildMsi();
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether the UIShell is demo mode.
        /// </summary>
        /// <value>
        /// <c>true</c> if this instance is demo mode; otherwise, <c>false</c>.
        /// </value>
        public bool IsDemoMode { get; set; }

        /// <summary>
        /// Proceeds to the next UI dialog.
        /// </summary>
        public void GoNext()
        {
            CurrentDialogIndex++;
        }

        /// <summary>
        /// Moves to the previous UI Dialog.
        /// </summary>
        public void GoPrev()
        {
            CurrentDialogIndex--;
        }

        /// <summary>
        /// Moves to the UI Dialog by the specified index in the <see cref="T:WixSharp.IManagedUIShell.Dialogs" /> sequence.
        /// </summary>
        /// <param name="index">The index.</param>
        public void GoTo(int index)
        {
            CurrentDialogIndex = index;
        }

        /// <summary>
        /// Moves to the UI Dialog by the specified dialog type in the <see cref="T:WixSharp.IManagedUIShell.Dialogs" /> sequence.
        /// </summary>
        public void GoTo<T>()
        {
            int index = Dialogs.FindIndex(t => t == typeof(T));
            if (index != -1)
                CurrentDialogIndex = index;
        }

        /// <summary>
        /// Moves to the UI Dialog by the last dialog in the <see cref="T:WixSharp.IManagedUIShell.Dialogs" /> sequence.
        /// </summary>
        public void GoToLast()
        {
            int index = Dialogs.Count - 1;
            if (index != -1)
                CurrentDialogIndex = index;
        }

        /// <summary>
        /// Exits this MSI UI application.
        /// </summary>
        public void Exit()
        {
            shellView.Close();
        }

        /// <summary>
        /// Cancels the MSI installation.
        /// </summary>
        public void Cancel()
        {
            if (!started)
                Exit();

            Runtime.CancelExecute?.Invoke();
        }

        /// <summary>
        /// Message dialog handler.
        /// </summary>
        public IManagedDialog MessageDialog { get; set; }

        internal class DefaultErrorDialog : IManagedDialog
        {
            public IManagedUIShell Shell { get; set; }

            private UIShell _shell;

            public DefaultErrorDialog(UIShell shell)
            {
                _shell = shell;
            }

            public void OnExecuteComplete()
            {
                throw new NotImplementedException();
            }

            public void OnExecuteStarted()
            {
                throw new NotImplementedException();
            }

            public void OnProgress(int progressPercentage)
            {
                throw new NotImplementedException();
            }

            public MessageResult ProcessMessage(InstallMessage messageType, Record messageRecord, MessageButtons buttons, MessageIcon icon, MessageDefaultButton defaultButton)
            {
                return (MessageResult)MessageBox.Show(
                    _shell.shellView,
                    messageRecord.ToString(),
                    "[ErrorDlg_Title]".LocalizeWith(_shell.Runtime.Localize),
                    (MessageBoxButtons)(int)buttons,
                    (MessageBoxIcon)(int)icon,
                    (MessageBoxDefaultButton)(int)defaultButton);
            }
        }

        /// <summary>
        /// Processes information and progress messages sent to the user interface.
        /// <para> This method directly mapped to the
        /// <see cref="T:Microsoft.Deployment.WindowsInstaller.IEmbeddedUI.ProcessMessage" />.</para>
        /// </summary>
        /// <param name="messageType">Type of the message.</param>
        /// <param name="messageRecord">The message record.</param>
        /// <param name="buttons">The buttons.</param>
        /// <param name="icon">The icon.</param>
        /// <param name="defaultButton">The default button.</param>
        /// <returns></returns>
        public MessageResult ProcessMessage(InstallMessage messageType, Record messageRecord, MessageButtons buttons, MessageIcon icon, MessageDefaultButton defaultButton)
        {
            var result = MessageResult.OK;
            try
            {
                UACRevealer.Exit();

                this.progressCounter.ProcessMessage(messageType, messageRecord);

                OnProgress((int)Math.Round(100 * this.progressCounter.Progress));

                switch (messageType)
                {
                    case InstallMessage.Progress: break;
                    case InstallMessage.Error:
                    case InstallMessage.Warning:
                    case InstallMessage.User:
                    case InstallMessage.Info:
                    default:
                        {
                            if (messageType.IsOneOf(InstallMessage.Error, InstallMessage.Warning, InstallMessage.User))
                            {
                                InUIThread(() =>
                                {
                                    if (MessageDialog == null)
                                        MessageDialog = new DefaultErrorDialog(this);

                                    result = MessageDialog.ProcessMessage(messageType, messageRecord, buttons, icon, defaultButton);
                                });
                                return result;
                            }

                            if (messageType == InstallMessage.Info)
                            {
                                string msg_data = messageRecord.ToString();
                                if (msg_data.Contains("User canceled installation") ||
                                    msg_data.Contains("User cancelled installation")) //there is no other way
                                    UserInterrupted = true;
                            }

                            if (messageType == InstallMessage.Error)
                            {
                                ErrorDetected = true;
                                Errors.Add(messageRecord.ToString());
                            }

                            if (messageType == InstallMessage.InstallEnd)
                            {
                                try
                                {
                                    string lastValue = messageRecord[messageRecord.FieldCount].ToString(); //MSI record is actually 1-based
                                    ErrorDetected = (lastValue == "3");
                                    UserInterrupted = (lastValue == "2");
                                }
                                catch { }//nothing we can do really
                                finished = true;
                            }

                            this.LogMessage("{0}: {1}", messageType, messageRecord);
                            break;
                        }
                }

                if (this.canceled)
                {
                    return MessageResult.Cancel;
                }
            }
            catch (Exception ex)
            {
                this.LogMessage(ex.ToString());
                this.LogMessage(ex.StackTrace);
            }

            InUIThread(() =>
            {
                if (CurrentDialog != null)
                    result = CurrentDialog.ProcessMessage(messageType, messageRecord, buttons, icon, defaultButton);
            });
            return result;
        }

        /// <summary>
        /// Gets the MSI log text.
        /// </summary>
        /// <value>
        /// The log.
        /// </value>
        public string Log { get { return InstallerRuntime.VirtualLog.ToString(); } }

        void LogMessage(string message, params object[] args)
        {
            InstallerRuntime.VirtualLog.AppendLine(message.FormatWith(args));
        }

        /// <summary>
        /// Called when MSI execute started.
        /// </summary>
        public void OnExecuteStarted()
        {
            //Debugger.Break();
            Runtime.FetchInstallDir(); //user may have updated it

            if (CurrentDialog != null)
                InUIThread(CurrentDialog.OnExecuteStarted);
        }

        /// <summary>
        /// Called when MSI execution is complete.
        /// </summary>
        public void OnExecuteComplete()
        {
            if (CurrentDialog != null)
                InUIThread(CurrentDialog.OnExecuteComplete);
        }

        /// <summary>
        /// Marshaling the action execution into UI thread
        /// </summary>
        /// <param name="action">The action.</param>
        public void InUIThread(System.Action action)
        {
            if (shellView != null)
                shellView.Invoke(action);
            else
                action();
        }

        /// <summary>
        /// Called when MSI execution progress is changed.
        /// </summary>
        /// <param name="progressPercentage">The progress percentage.</param>
        public void OnProgress(int progressPercentage)
        {
            if (CurrentDialog != null)
                InUIThread(() => CurrentDialog.OnProgress(progressPercentage));
        }
    }
}
