using System;
using System.Runtime.InteropServices;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace QuickOpenFile
{
    /// <summary>
    /// Visual Assist "Goto Implementation/Declaration" (Alt+G).
    /// 커서 위치의 심볼(변수, 함수, 클래스 등)에 대해 선언부 ↔ 구현부를 오간다.
    /// <para>
    /// 각 언어 서비스(C++ IntelliSense, Roslyn 등)의 탐색 명령을 순서대로 실행하고,
    /// 커서가 실제로 이동했는지 확인해서 이동하지 않았으면 다음 명령을 시도한다.
    /// </para>
    /// <list type="bullet">
    /// <item>C/C++: 사용처·선언 → 정의(구현)로, 정의(구현) 위 → 선언으로 (GoToDefinition → GoToDeclaration)</item>
    /// <item>그 외 언어: 사용처 → 정의로, 선언 위 → 구현으로 (GoToDefinition → GoToImplementation)</item>
    /// </list>
    /// </summary>
    internal static class GotoDeclImplCommand
    {
        // 비동기로 탐색하는 언어 서비스가 있으므로 이동 여부를 잠시 기다린다.
        private static readonly TimeSpan MoveTimeout = TimeSpan.FromMilliseconds(800);
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(40);

        // DTE.ExecuteCommand("이름") 은 명령 인수 파싱 때문에 "인수가 필요합니다" 오류가 날 수 있으므로
        // 명령은 항상 GUID/ID 로 실행한다.
        private static readonly string StandardCommandSet97 = VSConstants.GUID_VSStandardCommandSet97.ToString("B");

        private static readonly NavCommand GoToDefinition = new NavCommand(StandardCommandSet97, (int)VSConstants.VSStd97CmdID.GotoDefn, null);
        private static readonly NavCommand GoToDeclaration = new NavCommand(StandardCommandSet97, (int)VSConstants.VSStd97CmdID.GotoDecl, null);

        // Go To Implementation 은 SDK 상수가 없어 이름으로 GUID/ID 만 조회한다.
        private static readonly NavCommand GoToImplementation = new NavCommand(null, 0, "Edit.GoToImplementation");

        private static readonly NavCommand[] CppCommands = { GoToDefinition, GoToDeclaration };
        private static readonly NavCommand[] DefaultCommands = { GoToDefinition, GoToImplementation };

        private sealed class NavCommand
        {
            public NavCommand(string guid, int id, string name)
            {
                Guid = guid;
                Id = id;
                Name = name;
            }

            public string Guid { get; }

            public int Id { get; }

            public string Name { get; }
        }

        private static bool s_running;

        private readonly struct CaretLocation
        {
            public CaretLocation(string file, int line, int column)
            {
                File = file;
                Line = line;
                Column = column;
            }

            public string File { get; }

            public int Line { get; }

            public int Column { get; }

            public bool SameAs(CaretLocation other) =>
                string.Equals(File, other.File, StringComparison.OrdinalIgnoreCase) && Line == other.Line && Column == other.Column;
        }

        public static async Task ExecuteAsync(DTE dte, Action<string> setStatus)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (s_running)
            {
                return; // 키를 연타한 경우 중복 실행 방지
            }

            CaretLocation? start = GetCaret(dte);
            if (start == null)
            {
                setStatus("Goto Implementation/Declaration: 활성화된 코드 편집기가 없습니다.");
                return;
            }

            s_running = true;
            try
            {
                NavCommand[] commands = HeaderSourceResolver.IsCppFile(start.Value.File) ? CppCommands : DefaultCommands;
                foreach (NavCommand command in commands)
                {
                    if (!TryExecute(dte, command))
                    {
                        continue;
                    }

                    if (await WaitForMoveAsync(dte, start.Value))
                    {
                        return;
                    }
                }

                setStatus("Goto Implementation/Declaration: 커서 위치의 심볼에 대한 선언/구현을 찾지 못했습니다.");
            }
            finally
            {
                s_running = false;
            }
        }

        private static async System.Threading.Tasks.Task<bool> WaitForMoveAsync(DTE dte, CaretLocation start)
        {
            DateTime deadline = DateTime.UtcNow + MoveTimeout;
            while (true)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                CaretLocation? now = GetCaret(dte);
                if (now != null && !now.Value.SameAs(start))
                {
                    return true;
                }

                // 다른 창(찾기 결과, Peek 등)으로 포커스가 옮겨졌으면 탐색이 처리된 것으로 본다.
                if (now == null)
                {
                    return true;
                }

                if (DateTime.UtcNow >= deadline)
                {
                    return false;
                }

                await Task.Delay(PollInterval);
            }
        }

        private static bool TryExecute(DTE dte, NavCommand navCommand)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                Command command = navCommand.Name != null
                    ? dte.Commands.Item(navCommand.Name)
                    : dte.Commands.Item(navCommand.Guid, navCommand.Id);
                if (command == null || !command.IsAvailable)
                {
                    return false;
                }

                object customIn = null;
                object customOut = null;
                dte.Commands.Raise(command.Guid, command.ID, ref customIn, ref customOut);
                return true;
            }
            catch (Exception ex) when (ex is COMException || ex is ArgumentException)
            {
                // 명령이 없거나 현재 위치에서 실행할 수 없는 경우
                return false;
            }
        }

        private static CaretLocation? GetCaret(DTE dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                Document document = dte.ActiveDocument;
                if (document?.Selection is TextSelection selection)
                {
                    VirtualPoint point = selection.ActivePoint;
                    return new CaretLocation(document.FullName, point.Line, point.LineCharOffset);
                }
            }
            catch (Exception ex) when (ex is COMException || ex is ArgumentException || ex is InvalidOperationException)
            {
            }

            return null;
        }
    }
}
