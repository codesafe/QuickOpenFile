using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace QuickOpenFile
{
    /// <summary>
    /// 솔루션에 포함된 모든 파일 목록을 수집하고 캐시한다.
    /// 솔루션/프로젝트 열기·닫기, 파일 추가·삭제·이름 변경 시 캐시를 무효화한다.
    /// </summary>
    internal sealed class SolutionFileIndex : IVsSolutionEvents, IVsTrackProjectDocumentsEvents2, IDisposable
    {
        // C++ 프로젝트의 "External Dependencies" 가상 폴더(시스템 헤더 수천 개)는 제외한다.
        private static readonly HashSet<string> ExcludedVirtualFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "External Dependencies",
            "외부 종속성",
        };

        // 폴더 열기(Open Folder / CMake) 모드에서 디렉터리를 직접 스캔할 때 건너뛸 폴더
        private static readonly HashSet<string> SkippedScanDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".vs", ".git", ".svn", ".hg", "bin", "obj", "node_modules", "packages", "out", "build", "x64", "x86", "Debug", "Release", "ipch",
        };

        private const int MaxScannedFiles = 200000;

        private readonly IVsSolution _solution;
        private readonly IVsTrackProjectDocuments2 _trackDocuments;
        private uint _solutionEventsCookie;
        private uint _trackDocumentsCookie;

        private IReadOnlyList<SolutionFileEntry> _cache;

        private SolutionFileIndex(IVsSolution solution, IVsTrackProjectDocuments2 trackDocuments)
        {
            _solution = solution;
            _trackDocuments = trackDocuments;
        }

        public static async Task<SolutionFileIndex> CreateAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var solution = (IVsSolution)await package.GetServiceAsync(typeof(SVsSolution));
            var track = (IVsTrackProjectDocuments2)await package.GetServiceAsync(typeof(SVsTrackProjectDocuments));
            if (solution == null)
            {
                throw new InvalidOperationException("SVsSolution service is not available.");
            }

            var index = new SolutionFileIndex(solution, track);
            solution.AdviseSolutionEvents(index, out index._solutionEventsCookie);
            track?.AdviseTrackProjectDocumentsEvents(index, out index._trackDocumentsCookie);
            return index;
        }

        public void Invalidate() => _cache = null;

        /// <summary>현재 솔루션의 파일 목록. 캐시가 유효하면 즉시 반환한다.</summary>
        public async Task<IReadOnlyList<SolutionFileEntry>> GetFilesAsync()
        {
            IReadOnlyList<SolutionFileEntry> cached = _cache;
            if (cached != null)
            {
                return cached;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            string rootDirectory = GetSolutionDirectory();
            List<SolutionFileEntry> files = CollectFromHierarchies(rootDirectory);

            if (files.Count == 0 && !string.IsNullOrEmpty(rootDirectory) && Directory.Exists(rootDirectory))
            {
                // 프로젝트가 없는 경우(폴더 열기 모드 등) 디렉터리를 직접 스캔한다.
                files = await Task.Run(() => ScanDirectory(rootDirectory));
            }

            _cache = files;
            return files;
        }

        public string GetSolutionDirectory()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_solution.GetSolutionInfo(out string dir, out string _, out string _) == VSConstants.S_OK && !string.IsNullOrEmpty(dir))
            {
                return dir.TrimEnd('\\');
            }

            return string.Empty;
        }

        #region 계층(Hierarchy) 순회

        private List<SolutionFileEntry> CollectFromHierarchies(string rootDirectory)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var result = new List<SolutionFileEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Guid empty = Guid.Empty;
            if (_solution.GetProjectEnum((uint)__VSENUMPROJFLAGS.EPF_LOADEDINSOLUTION, ref empty, out IEnumHierarchies hierarchies) != VSConstants.S_OK ||
                hierarchies == null)
            {
                return result;
            }

            var buffer = new IVsHierarchy[1];
            while (hierarchies.Next(1, buffer, out uint fetched) == VSConstants.S_OK && fetched == 1)
            {
                IVsHierarchy hierarchy = buffer[0];
                if (hierarchy == null)
                {
                    continue;
                }

                try
                {
                    CollectFromHierarchy(hierarchy, rootDirectory, seen, result);
                }
                catch (Exception ex) when (!ErrorHandler.IsCriticalException(ex))
                {
                    // 특정 프로젝트 형식에서 실패하더라도 나머지 프로젝트는 계속 처리한다.
                    System.Diagnostics.Debug.WriteLine($"QuickOpenFile: {ex}");
                }
            }

            return result;
        }

        private static void CollectFromHierarchy(IVsHierarchy hierarchy, string rootDirectory, HashSet<string> seen, List<SolutionFileEntry> result)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string projectName = GetString(hierarchy, VSConstants.VSITEMID_ROOT, __VSHPROPID.VSHPROPID_Name) ?? string.Empty;

            var stack = new Stack<uint>();
            PushIfValid(stack, GetItemId(hierarchy, VSConstants.VSITEMID_ROOT, __VSHPROPID.VSHPROPID_FirstChild));

            Guid iidHierarchy = typeof(IVsHierarchy).GUID;
            while (stack.Count > 0)
            {
                uint itemId = stack.Pop();
                PushIfValid(stack, GetItemId(hierarchy, itemId, __VSHPROPID.VSHPROPID_NextSibling));

                // 하위 프로젝트(솔루션 폴더 안의 프로젝트)는 GetProjectEnum 에서 따로 열거되므로 건너뛴다.
                if (hierarchy.GetNestedHierarchy(itemId, ref iidHierarchy, out IntPtr nested, out uint _) == VSConstants.S_OK &&
                    nested != IntPtr.Zero)
                {
                    Marshal.Release(nested);
                    continue;
                }

                if (GetBool(hierarchy, itemId, __VSHPROPID.VSHPROPID_IsNonMemberItem) ||
                    GetBool(hierarchy, itemId, __VSHPROPID.VSHPROPID_IsHiddenItem))
                {
                    continue;
                }

                string name = GetString(hierarchy, itemId, __VSHPROPID.VSHPROPID_Name);
                if (name != null && ExcludedVirtualFolders.Contains(name))
                {
                    continue;
                }

                if (hierarchy.GetCanonicalName(itemId, out string path) == VSConstants.S_OK && IsFilePath(path) && seen.Add(path))
                {
                    result.Add(new SolutionFileEntry(path, projectName, rootDirectory));
                }

                PushIfValid(stack, GetItemId(hierarchy, itemId, __VSHPROPID.VSHPROPID_FirstChild));
            }
        }

        private static bool IsFilePath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.EndsWith("\\", StringComparison.Ordinal) || path.EndsWith("/", StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                if (!Path.IsPathRooted(path) || path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                // 확장자가 없는 항목은 폴더일 가능성이 있으므로 실제로 확인한다.
                return Path.HasExtension(path) || !Directory.Exists(path);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static void PushIfValid(Stack<uint> stack, uint itemId)
        {
            if (itemId != VSConstants.VSITEMID_NIL)
            {
                stack.Push(itemId);
            }
        }

        private static uint GetItemId(IVsHierarchy hierarchy, uint itemId, __VSHPROPID property)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (hierarchy.GetProperty(itemId, (int)property, out object value) != VSConstants.S_OK || value == null)
            {
                return VSConstants.VSITEMID_NIL;
            }

            switch (value)
            {
                case int i: return unchecked((uint)i);
                case uint u: return u;
                case short s: return unchecked((uint)s);
                case long l: return unchecked((uint)l);
                default: return VSConstants.VSITEMID_NIL;
            }
        }

        private static string GetString(IVsHierarchy hierarchy, uint itemId, __VSHPROPID property)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return hierarchy.GetProperty(itemId, (int)property, out object value) == VSConstants.S_OK ? value as string : null;
        }

        private static bool GetBool(IVsHierarchy hierarchy, uint itemId, __VSHPROPID property)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return hierarchy.GetProperty(itemId, (int)property, out object value) == VSConstants.S_OK && value is bool b && b;
        }

        #endregion

        #region 폴더 스캔

        private static List<SolutionFileEntry> ScanDirectory(string root)
        {
            var result = new List<SolutionFileEntry>();
            var pending = new Stack<string>();
            pending.Push(root);

            while (pending.Count > 0 && result.Count < MaxScannedFiles)
            {
                string dir = pending.Pop();
                try
                {
                    foreach (string file in Directory.EnumerateFiles(dir))
                    {
                        result.Add(new SolutionFileEntry(file, string.Empty, root));
                    }

                    foreach (string sub in Directory.EnumerateDirectories(dir))
                    {
                        string name = Path.GetFileName(sub);
                        if (!SkippedScanDirectories.Contains(name) &&
                            (new DirectoryInfo(sub).Attributes & FileAttributes.Hidden) == 0)
                        {
                            pending.Push(sub);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                }
            }

            return result;
        }

        #endregion

        public void Dispose()
        {
            if (!ThreadHelper.CheckAccess())
            {
                return;
            }
#pragma warning disable VSTHRD010 // 바로 위에서 UI 스레드인지 확인했다.

            if (_solutionEventsCookie != 0)
            {
                _solution.UnadviseSolutionEvents(_solutionEventsCookie);
                _solutionEventsCookie = 0;
            }

            if (_trackDocumentsCookie != 0)
            {
                _trackDocuments?.UnadviseTrackProjectDocumentsEvents(_trackDocumentsCookie);
                _trackDocumentsCookie = 0;
            }
#pragma warning restore VSTHRD010
        }

        #region IVsSolutionEvents

        int IVsSolutionEvents.OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded) { Invalidate(); return VSConstants.S_OK; }

        int IVsSolutionEvents.OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) => VSConstants.S_OK;

        int IVsSolutionEvents.OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved) { Invalidate(); return VSConstants.S_OK; }

        int IVsSolutionEvents.OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy) { Invalidate(); return VSConstants.S_OK; }

        int IVsSolutionEvents.OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel) => VSConstants.S_OK;

        int IVsSolutionEvents.OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) { Invalidate(); return VSConstants.S_OK; }

        int IVsSolutionEvents.OnAfterOpenSolution(object pUnkReserved, int fNewSolution) { Invalidate(); return VSConstants.S_OK; }

        int IVsSolutionEvents.OnQueryCloseSolution(object pUnkReserved, ref int pfCancel) => VSConstants.S_OK;

        int IVsSolutionEvents.OnBeforeCloseSolution(object pUnkReserved) => VSConstants.S_OK;

        int IVsSolutionEvents.OnAfterCloseSolution(object pUnkReserved) { Invalidate(); return VSConstants.S_OK; }

        #endregion

        #region IVsTrackProjectDocumentsEvents2

        int IVsTrackProjectDocumentsEvents2.OnQueryAddFiles(IVsProject pProject, int cFiles, string[] rgpszMkDocuments, VSQUERYADDFILEFLAGS[] rgFlags, VSQUERYADDFILERESULTS[] pSummaryResult, VSQUERYADDFILERESULTS[] rgResults) => VSConstants.S_OK;

        int IVsTrackProjectDocumentsEvents2.OnAfterAddFilesEx(int cProjects, int cFiles, IVsProject[] rgpProjects, int[] rgFirstIndices, string[] rgpszMkDocuments, VSADDFILEFLAGS[] rgFlags) { Invalidate(); return VSConstants.S_OK; }

        int IVsTrackProjectDocumentsEvents2.OnAfterAddDirectoriesEx(int cProjects, int cDirectories, IVsProject[] rgpProjects, int[] rgFirstIndices, string[] rgpszMkDocuments, VSADDDIRECTORYFLAGS[] rgFlags) { Invalidate(); return VSConstants.S_OK; }

        int IVsTrackProjectDocumentsEvents2.OnAfterRemoveFiles(int cProjects, int cFiles, IVsProject[] rgpProjects, int[] rgFirstIndices, string[] rgpszMkDocuments, VSREMOVEFILEFLAGS[] rgFlags) { Invalidate(); return VSConstants.S_OK; }

        int IVsTrackProjectDocumentsEvents2.OnAfterRemoveDirectories(int cProjects, int cDirectories, IVsProject[] rgpProjects, int[] rgFirstIndices, string[] rgpszMkDocuments, VSREMOVEDIRECTORYFLAGS[] rgFlags) { Invalidate(); return VSConstants.S_OK; }

        int IVsTrackProjectDocumentsEvents2.OnQueryRenameFiles(IVsProject pProject, int cFiles, string[] rgszMkOldNames, string[] rgszMkNewNames, VSQUERYRENAMEFILEFLAGS[] rgFlags, VSQUERYRENAMEFILERESULTS[] pSummaryResult, VSQUERYRENAMEFILERESULTS[] rgResults) => VSConstants.S_OK;

        int IVsTrackProjectDocumentsEvents2.OnAfterRenameFiles(int cProjects, int cFiles, IVsProject[] rgpProjects, int[] rgFirstIndices, string[] rgszMkOldNames, string[] rgszMkNewNames, VSRENAMEFILEFLAGS[] rgFlags) { Invalidate(); return VSConstants.S_OK; }

        int IVsTrackProjectDocumentsEvents2.OnQueryRenameDirectories(IVsProject pProject, int cDirs, string[] rgszMkOldNames, string[] rgszMkNewNames, VSQUERYRENAMEDIRECTORYFLAGS[] rgFlags, VSQUERYRENAMEDIRECTORYRESULTS[] pSummaryResult, VSQUERYRENAMEDIRECTORYRESULTS[] rgResults) => VSConstants.S_OK;

        int IVsTrackProjectDocumentsEvents2.OnAfterRenameDirectories(int cProjects, int cDirs, IVsProject[] rgpProjects, int[] rgFirstIndices, string[] rgszMkOldNames, string[] rgszMkNewNames, VSRENAMEDIRECTORYFLAGS[] rgFlags) { Invalidate(); return VSConstants.S_OK; }

        int IVsTrackProjectDocumentsEvents2.OnQueryAddDirectories(IVsProject pProject, int cDirectories, string[] rgpszMkDocuments, VSQUERYADDDIRECTORYFLAGS[] rgFlags, VSQUERYADDDIRECTORYRESULTS[] pSummaryResult, VSQUERYADDDIRECTORYRESULTS[] rgResults) => VSConstants.S_OK;

        int IVsTrackProjectDocumentsEvents2.OnQueryRemoveFiles(IVsProject pProject, int cFiles, string[] rgpszMkDocuments, VSQUERYREMOVEFILEFLAGS[] rgFlags, VSQUERYREMOVEFILERESULTS[] pSummaryResult, VSQUERYREMOVEFILERESULTS[] rgResults) => VSConstants.S_OK;

        int IVsTrackProjectDocumentsEvents2.OnQueryRemoveDirectories(IVsProject pProject, int cDirectories, string[] rgpszMkDocuments, VSQUERYREMOVEDIRECTORYFLAGS[] rgFlags, VSQUERYREMOVEDIRECTORYRESULTS[] pSummaryResult, VSQUERYREMOVEDIRECTORYRESULTS[] rgResults) => VSConstants.S_OK;

        int IVsTrackProjectDocumentsEvents2.OnAfterSccStatusChanged(int cProjects, int cFiles, IVsProject[] rgpProjects, int[] rgFirstIndices, string[] rgpszMkDocuments, uint[] rgdwSccStatus) => VSConstants.S_OK;

        #endregion
    }
}
