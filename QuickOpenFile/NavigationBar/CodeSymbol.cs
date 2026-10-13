using System;
using System.Collections.Generic;

namespace QuickOpenFile.NavigationBar
{
    internal enum CodeSymbolKind
    {
        Namespace,
        Type,

        /// <summary>함수, C# 속성 / 이벤트 / 인덱서.</summary>
        Function,
    }

    /// <summary>
    /// 파일 안의 네임스페이스 / 클래스 / 함수 하나 (<see cref="ScopeParser"/> 가 만든 값).
    /// VS API 에 의존하지 않으므로 백그라운드 스레드에서 만들고 읽을 수 있다.
    /// </summary>
    internal sealed class CodeSymbol
    {
        private readonly string _separator;
        private string _displayText;

        public CodeSymbol(CodeSymbolKind kind, string name, string fullName, string parameters, int startLine, CodeSymbol parent, string separator)
        {
            Kind = kind;
            Name = name;
            FullName = fullName;
            Parameters = parameters;
            StartLine = startLine;
            EndLine = startLine;
            Parent = parent;
            Depth = parent == null ? 0 : parent.Depth + 1;
            _separator = separator;
        }

        public CodeSymbolKind Kind { get; }

        /// <summary>마지막 이름 (예: <c>Bar</c>).</summary>
        public string Name { get; }

        /// <summary>예: <c>MyNamespace::Foo::Bar</c> (C++), <c>MyNamespace.Foo.Bar</c> (C#)</summary>
        public string FullName { get; }

        /// <summary>함수의 매개변수 목록 (괄호 제외). 함수가 아니면 null.</summary>
        public string Parameters { get; }

        /// <summary>1부터 시작하는 줄 번호.</summary>
        public int StartLine { get; }

        public int EndLine { get; internal set; }

        public int Depth { get; }

        public CodeSymbol Parent { get; }

        public bool Contains(int line) => StartLine <= line && line <= EndLine;

        /// <summary>
        /// Context 칸에 표시할 범위 이름.
        /// C++ 은 전체 이름(ns::Foo::Bar), C# 은 네임스페이스가 길어 함수 이름이 잘리지 않도록 생략한다 (Foo.Bar).
        /// </summary>
        public string ContextText =>
            _separator == "::" || Kind == CodeSymbolKind.Namespace ? FullName : StripNamespaces(FullName);

        /// <summary>
        /// 함수 목록에 표시할 이름. 클래스 안의 멤버는 이름만, 그 외에는 네임스페이스를 뺀 이름(Foo::Bar)을 쓰고,
        /// 함수는 매개변수 목록을 붙인다.
        /// </summary>
        public string GetDisplayText()
        {
            if (_displayText == null)
            {
                string name = Parent != null && Parent.Kind == CodeSymbolKind.Type ? Name : StripNamespaces(FullName);
                _displayText = Parameters != null ? name + "(" + Parameters + ")" : name;
            }

            return _displayText;
        }

        /// <summary>커서 줄을 포함하는 가장 안쪽 심볼.</summary>
        public static CodeSymbol FindInnermost(IReadOnlyList<CodeSymbol> symbols, int line)
        {
            CodeSymbol best = null;
            if (symbols == null)
            {
                return null;
            }

            foreach (CodeSymbol symbol in symbols)
            {
                if (symbol.Contains(line) &&
                    (best == null || symbol.Depth > best.Depth ||
                     (symbol.Depth == best.Depth && symbol.EndLine - symbol.StartLine < best.EndLine - best.StartLine)))
                {
                    best = symbol;
                }
            }

            return best;
        }

        // 네임스페이스를 생략한다 (Visual Assist 와 같은 표시).
        private string StripNamespaces(string fullName)
        {
            CodeSymbol ns = Parent;
            while (ns != null && ns.Kind != CodeSymbolKind.Namespace)
            {
                ns = ns.Parent;
            }

            string prefix = ns == null ? null : ns.FullName + _separator;
            return prefix != null && fullName.StartsWith(prefix, StringComparison.Ordinal)
                ? fullName.Substring(prefix.Length)
                : fullName;
        }

        public override string ToString() => $"{Kind} {FullName} [{StartLine}-{EndLine}]";
    }
}
