using System;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;

namespace QuickOpenFile.NavigationBar
{
    /// <summary>
    /// Definition 칸(읽기 전용 텍스트 상자)에 포커스가 있을 때 편집기 명령을 가로챈다.
    /// <para>
    /// 막대는 편집기 창의 일부라서 Ctrl+C 같은 단축키가 VS 명령(Edit.Copy)으로 바뀌어 편집기에 전달되고,
    /// 그대로 두면 텍스트 상자 대신 코드 줄이 복사된다. 그래서:
    /// </para>
    /// <list type="bullet">
    /// <item>복사 / 모두 선택: 텍스트 상자에 대해 실행한다.</item>
    /// <item>편집기 이동 · 편집 명령(방향키, Shift+방향키, Home/End, 붙여넣기, 실행 취소 등): 비활성으로 알려
    /// VS 가 단축키를 소비하지 않게 하여 키 입력이 텍스트 상자로 가도록 한다.</item>
    /// </list>
    /// 그 밖의 명령(저장, 빌드 등)과 포커스가 텍스트 상자에 없을 때는 그대로 다음 필터로 넘긴다.
    /// </summary>
    internal sealed class DefinitionCommandFilter : IOleCommandTarget
    {
        private readonly Func<bool> _isActive;
        private readonly Action _copy;
        private readonly Action _selectAll;

        public DefinitionCommandFilter(Func<bool> isActive, Action copy, Action selectAll)
        {
            _isActive = isActive;
            _copy = copy;
            _selectAll = selectAll;
        }

        public IOleCommandTarget Next { get; set; }

        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_isActive() && cCmds == 1)
            {
                switch (Classify(pguidCmdGroup, prgCmds[0].cmdID))
                {
                    case Handling.Copy:
                    case Handling.SelectAll:
                        prgCmds[0].cmdf = (uint)(OLECMDF.OLECMDF_SUPPORTED | OLECMDF.OLECMDF_ENABLED);
                        return VSConstants.S_OK;
                    case Handling.Block:
                        prgCmds[0].cmdf = (uint)OLECMDF.OLECMDF_SUPPORTED; // 비활성: 단축키가 텍스트 상자로 간다.
                        return VSConstants.S_OK;
                }
            }

            return Next?.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText) ?? (int)Constants.OLECMDERR_E_NOTSUPPORTED;
        }

        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_isActive())
            {
                switch (Classify(pguidCmdGroup, nCmdID))
                {
                    case Handling.Copy:
                        _copy();
                        return VSConstants.S_OK;
                    case Handling.SelectAll:
                        _selectAll();
                        return VSConstants.S_OK;
                    case Handling.Block:
                        return VSConstants.S_OK; // 편집기 커서 / 내용이 바뀌지 않게 무시
                }
            }

            return Next?.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut) ?? (int)Constants.OLECMDERR_E_NOTSUPPORTED;
        }

        private enum Handling
        {
            PassThrough,
            Copy,
            SelectAll,
            Block,
        }

        private static Handling Classify(Guid group, uint id)
        {
            if (group == VSConstants.GUID_VSStandardCommandSet97)
            {
                switch ((VSConstants.VSStd97CmdID)id)
                {
                    case VSConstants.VSStd97CmdID.Copy:
                        return Handling.Copy;
                    case VSConstants.VSStd97CmdID.SelectAll:
                        return Handling.SelectAll;
                    case VSConstants.VSStd97CmdID.Cut:
                    case VSConstants.VSStd97CmdID.Paste:
                    case VSConstants.VSStd97CmdID.Delete:
                    case VSConstants.VSStd97CmdID.Undo:
                    case VSConstants.VSStd97CmdID.Redo:
                    case VSConstants.VSStd97CmdID.MultiLevelUndo:
                    case VSConstants.VSStd97CmdID.MultiLevelRedo:
                        return Handling.Block;
                }

                return Handling.PassThrough;
            }

            // 편집기 전용 명령 (방향키, 선택 확장, 문자 입력, 줄 삭제 등)
            if (group == VSConstants.VSStd2K)
            {
                return (VSConstants.VSStd2KCmdID)id == VSConstants.VSStd2KCmdID.COPY ? Handling.Copy : Handling.Block;
            }

            return Handling.PassThrough;
        }
    }
}
