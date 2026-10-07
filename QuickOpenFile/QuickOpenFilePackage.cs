using System;
using System.ComponentModel.Design;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace QuickOpenFile
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true, RegisterUsing = RegistrationMethod.CodeBase)]
    [InstalledProductRegistration("Quick Open File", "Visual Assist style Open File in Solution / Header-Source toggle", "1.0.2")]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    // 솔루션이 열리면 백그라운드로 로드해서 파일 변경 이벤트를 받고, 메뉴 표시 여부를 갱신한다.
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
    [Guid(PackageGuids.PackageString)]
    public sealed class QuickOpenFilePackage : AsyncPackage
    {
        private SolutionFileIndex _index;
        private DTE _dte;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            _dte = (DTE)await GetServiceAsync(typeof(DTE));
            _index = await SolutionFileIndex.CreateAsync(this);

            if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commandService)
            {
                var openCommand = new OleMenuCommand(OnOpenFileInSolution, new CommandID(PackageGuids.CmdSet, PackageIds.OpenFileInSolution));
                commandService.AddCommand(openCommand);

                var toggleCommand = new OleMenuCommand(OnToggleHeaderSource, new CommandID(PackageGuids.CmdSet, PackageIds.ToggleHeaderSource));
                toggleCommand.BeforeQueryStatus += OnToggleHeaderSourceQueryStatus;
                commandService.AddCommand(toggleCommand);

                var gotoCommand = new OleMenuCommand(OnGotoDeclImpl, new CommandID(PackageGuids.CmdSet, PackageIds.GotoDeclImpl));
                commandService.AddCommand(gotoCommand);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _index != null)
            {
#pragma warning disable VSTHRD010 // 패키지 Dispose 는 UI 스레드에서 호출된다.
                _index.Dispose();
#pragma warning restore VSTHRD010
                _index = null;
            }

            base.Dispose(disposing);
        }

        #region Open File in Solution (Alt+Shift+O)

        private void OnOpenFileInSolution(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var dialog = new QuickOpenDialog(async force =>
            {
                if (force)
                {
                    _index.Invalidate();
                }

                return await _index.GetFilesAsync();
            });

            if (dialog.ShowModal() == true)
            {
                foreach (SolutionFileEntry file in dialog.SelectedFiles)
                {
                    DocumentOpener.Open(file.FullPath);
                }
            }
        }

        #endregion

        #region Header <-> Source (Alt+O)

        private void OnToggleHeaderSourceQueryStatus(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (sender is OleMenuCommand command)
            {
                bool isCpp = HeaderSourceResolver.IsCppFile(GetActiveDocumentPath());
                command.Visible = isCpp;
                command.Enabled = isCpp;
            }
        }

        private void OnToggleHeaderSource(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string current = GetActiveDocumentPath();
            if (!HeaderSourceResolver.IsCppFile(current))
            {
                SetStatusBarText("Open Corresponding File: 현재 문서가 C/C++ 파일이 아닙니다.");
                return;
            }

            _ = JoinableTaskFactory.RunAsync(async () =>
            {
                var files = await _index.GetFilesAsync();
                await JoinableTaskFactory.SwitchToMainThreadAsync();

                string target = HeaderSourceResolver.FindCounterpart(current, files);
                if (target == null)
                {
                    SetStatusBarText($"Open Corresponding File: '{Path.GetFileName(current)}' 에 대응하는 파일을 찾지 못했습니다.");
                    return;
                }

                DocumentOpener.Open(target);
            });
        }

        #endregion

        #region Goto Implementation/Declaration (Alt+G)

        private void OnGotoDeclImpl(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            _ = JoinableTaskFactory.RunAsync(() => GotoDeclImplCommand.ExecuteAsync(_dte, SetStatusBarText));
        }

        #endregion

        #region 공통

        private string GetActiveDocumentPath()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                return _dte?.ActiveDocument?.FullName;
            }
            catch (Exception ex) when (ex is COMException || ex is ArgumentException)
            {
                return null;
            }
        }

        private void SetStatusBarText(string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (GetService(typeof(SVsStatusbar)) is IVsStatusbar statusBar)
            {
                statusBar.IsFrozen(out int frozen);
                if (frozen == 0)
                {
                    statusBar.SetText(text);
                }
            }
        }

        #endregion
    }

    internal static class DocumentOpener
    {
        public static void Open(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, path, Guid.Empty, out _, out _, out IVsWindowFrame frame);
                frame?.Show();
            }
            catch (Exception ex) when (!ErrorHandler.IsCriticalException(ex))
            {
                VsShellUtilities.ShowMessageBox(
                    ServiceProvider.GlobalProvider,
                    $"파일을 열 수 없습니다:\n{path}\n\n{ex.Message}",
                    "Quick Open File",
                    OLEMSGICON.OLEMSGICON_WARNING,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            }
        }
    }
}
