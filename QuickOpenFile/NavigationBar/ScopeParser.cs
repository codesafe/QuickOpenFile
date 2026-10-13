using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace QuickOpenFile.NavigationBar
{
    internal enum SourceLanguage
    {
        Cpp,
        CSharp,
    }

    /// <summary>
    /// C/C++ · C# 소스에서 네임스페이스 / 클래스 / 함수의 범위(시작~끝 줄)를 찾는 경량 파서.
    /// <para>
    /// VS 코드 모델(EnvDTE)은 UI 스레드에서만 쓸 수 있고 IntelliSense 분석이 끝날 때까지 UI 를 멈추게 하므로,
    /// Visual Assist 처럼 자체 파서로 텍스트만 보고 백그라운드 스레드에서 계산한다.
    /// </para>
    /// <para>
    /// 주석 · 문자열 · 전처리기 줄을 건너뛰고 중괄호를 추적한다. #if / #else 는 첫 번째 분기만 읽는다.
    /// 정확한 의미 분석이 아닌 표시용 휴리스틱이므로 복잡한 매크로 등에서는 틀릴 수 있다.
    /// </para>
    /// </summary>
    internal static class ScopeParser
    {
        private enum TokenKind
        {
            Identifier,
            Punct,
            Literal,
        }

        private struct Token
        {
            public TokenKind Kind;
            public string Text;
            public int Line;
            public int Start;
            public int End;

            public bool Is(string text) => Kind != TokenKind.Literal && Text == text;

            public override string ToString() => Text;
        }

        private enum FrameKind
        {
            /// <summary>전역 / 네임스페이스 / 클래스 본문 / extern "C" — 선언을 찾는다.</summary>
            Declarative,

            /// <summary>함수 본문, 초기화 식 등 — 중괄호만 센다.</summary>
            Code,

            /// <summary>C++ 생성자 초기화 목록의 중괄호 초기화 (m_x{1}) — 닫히면 원래 선언을 이어서 읽는다.</summary>
            InitBrace,
        }

        private sealed class Frame
        {
            public FrameKind Kind;
            public CodeSymbol Symbol;
            public List<Token> SavedBuffer;
        }

        private static readonly HashSet<string> CppTypeKeywords = new HashSet<string> { "class", "struct", "union", "enum", "__interface" };
        private static readonly HashSet<string> CSharpTypeKeywords = new HashSet<string> { "class", "struct", "interface", "enum", "record" };

        // '(' 앞에 와도 함수 이름이 아닌 것
        private static readonly HashSet<string> NonFunctionNames = new HashSet<string>
        {
            "if", "for", "while", "switch", "catch", "return", "sizeof", "alignof", "decltype", "noexcept", "throw", "alignas",
            "__declspec", "__attribute__", "requires", "static_assert", "defined", "__pragma", "_Pragma", "typeid",
            "new", "typeof", "nameof", "using", "lock", "foreach", "fixed", "checked", "unchecked", "default", "when", "base", "this",

            // 함수 포인터 / 캐스트: void (*cb)(int), int (Foo::*pm)()
            "void", "int", "char", "bool", "float", "double", "long", "short", "unsigned", "signed", "auto", "const", "volatile",
            "static", "virtual", "inline", "explicit", "extern", "typedef",
        };

        // 클래스 이름 뒤에 올 수 있는 한정자
        private static readonly HashSet<string> TypeNameModifiers = new HashSet<string> { "final", "sealed", "abstract" };

        public static List<CodeSymbol> Parse(string text, SourceLanguage language)
        {
            List<Token> tokens = Tokenize(text ?? string.Empty, language);
            string separator = language == SourceLanguage.CSharp ? "." : "::";

            var result = new List<CodeSymbol>();
            var stack = new Stack<Frame>();
            var buffer = new List<Token>();
            CodeSymbol fileNamespace = null; // C# 파일 범위 네임스페이스 (namespace X;)
            int lastLine = tokens.Count > 0 ? tokens[tokens.Count - 1].Line : 1;

            foreach (Token token in tokens)
            {
                bool declarative = stack.Count == 0 || stack.Peek().Kind == FrameKind.Declarative;

                if (token.Is("{"))
                {
                    if (!declarative)
                    {
                        stack.Push(new Frame { Kind = FrameKind.Code });
                        continue;
                    }

                    CodeSymbol parent = CurrentParent(stack) ?? fileNamespace;
                    Frame frame = Classify(buffer, parent, separator, language);
                    if (frame.Symbol != null)
                    {
                        result.Add(frame.Symbol);
                    }

                    if (frame.Kind == FrameKind.InitBrace)
                    {
                        frame.SavedBuffer = new List<Token>(buffer);
                    }

                    stack.Push(frame);
                    buffer.Clear();
                }
                else if (token.Is("}"))
                {
                    if (stack.Count == 0)
                    {
                        buffer.Clear();
                        continue;
                    }

                    Frame frame = stack.Pop();
                    if (frame.Symbol != null)
                    {
                        frame.Symbol.EndLine = token.Line;
                    }

                    if (frame.Kind == FrameKind.InitBrace)
                    {
                        buffer = frame.SavedBuffer;
                        buffer.Add(token);
                    }
                    else if (stack.Count == 0 || stack.Peek().Kind == FrameKind.Declarative)
                    {
                        buffer.Clear();
                    }
                }
                else if (token.Is(";"))
                {
                    if (declarative)
                    {
                        CodeSymbol parent = CurrentParent(stack) ?? fileNamespace;
                        CodeSymbol symbol = ClassifyStatement(buffer, token, parent, separator, language);
                        if (symbol != null)
                        {
                            result.Add(symbol);
                            if (symbol.Kind == CodeSymbolKind.Namespace)
                            {
                                fileNamespace = symbol;
                            }
                        }

                        buffer.Clear();
                    }
                }
                else if (declarative)
                {
                    buffer.Add(token);
                }
            }

            // 닫히지 않은 범위(파일 범위 네임스페이스, 짝이 안 맞는 중괄호)는 파일 끝까지로 본다.
            foreach (CodeSymbol symbol in result)
            {
                if (symbol.EndLine < symbol.StartLine || (symbol.Kind == CodeSymbolKind.Namespace && symbol == fileNamespace))
                {
                    symbol.EndLine = lastLine;
                }
            }

            foreach (Frame frame in stack)
            {
                if (frame.Symbol != null)
                {
                    frame.Symbol.EndLine = lastLine;
                }
            }

            result.Sort((a, b) => a.StartLine != b.StartLine ? a.StartLine.CompareTo(b.StartLine) : a.Depth.CompareTo(b.Depth));
            return result;
        }

        private static CodeSymbol CurrentParent(Stack<Frame> stack)
        {
            foreach (Frame frame in stack)
            {
                if (frame.Symbol != null)
                {
                    return frame.Symbol;
                }
            }

            return null;
        }

        #region '{' 앞의 선언 분류

        private static Frame Classify(List<Token> buffer, CodeSymbol parent, string separator, SourceLanguage language)
        {
            var code = new Frame { Kind = FrameKind.Code };
            if (buffer.Count == 0)
            {
                return code;
            }

            // namespace
            int nsIndex = buffer.FindIndex(t => t.Is("namespace"));
            if (nsIndex >= 0)
            {
                string name = ReadQualifiedName(buffer, nsIndex + 1, separator) ?? "(anonymous)";
                return Declarative(CodeSymbolKind.Namespace, name, null, buffer[nsIndex].Line, parent, separator);
            }

            // extern "C" { ... }
            if (buffer[0].Is("extern") && buffer.Skip(1).All(t => t.Kind == TokenKind.Literal))
            {
                return new Frame { Kind = FrameKind.Declarative };
            }

            // C# 식 본문 안의 중괄호 (=> x switch { ... }, => list.Select(x => { ... })): '=>' 앞부분만 선언으로 본다.
            if (language == SourceLanguage.CSharp)
            {
                int arrow = buffer.FindIndex(t => t.Is("=>"));
                if (arrow == 0)
                {
                    return code;
                }

                if (arrow > 0)
                {
                    buffer = buffer.GetRange(0, arrow);
                }
            }

            ScanTopLevel(buffer, language, out int assignIndex, out int typeKeyIndex, out List<int> parens, out int initColonIndex);

            // 초기화 식 (int a[] = { ... }, auto f = [] { ... })
            if (assignIndex >= 0)
            {
                return code;
            }

            // class / struct / enum ...
            if (typeKeyIndex >= 0 && (parens.Count == 0 || parens[0] > typeKeyIndex))
            {
                string name = ReadTypeName(buffer, typeKeyIndex, language) ?? "(anonymous)";
                return Declarative(CodeSymbolKind.Type, name, null, buffer[typeKeyIndex].Line, parent, separator);
            }

            // 함수 정의
            if (TryReadFunction(buffer, parens, initColonIndex, separator, out string qualified, out string parameters, out int nameLine, out _))
            {
                // C++ 생성자 초기화 목록 안의 중괄호 초기화: Foo() : m_a{1}, m_b{2} { ... }
                if (language == SourceLanguage.Cpp && initColonIndex >= 0)
                {
                    Token last = buffer[buffer.Count - 1];
                    if (last.Kind == TokenKind.Identifier || last.Is(">"))
                    {
                        return new Frame { Kind = FrameKind.InitBrace };
                    }
                }

                return Body(CodeSymbolKind.Function, qualified, parameters, nameLine, parent, separator);
            }

            // C# 속성 / 이벤트 / 인덱서: public int Count { get; set; }
            if (language == SourceLanguage.CSharp && parent != null && parent.Kind == CodeSymbolKind.Type && buffer.Count >= 2)
            {
                Token last = buffer[buffer.Count - 1];
                if (last.Kind == TokenKind.Identifier)
                {
                    return Body(CodeSymbolKind.Function, last.Text, null, last.Line, parent, separator);
                }

                if (last.Is("]"))
                {
                    int thisIndex = buffer.FindLastIndex(t => t.Is("this"));
                    if (thisIndex >= 0)
                    {
                        return Body(CodeSymbolKind.Function, "this[]", null, buffer[thisIndex].Line, parent, separator);
                    }
                }
            }

            return code;
        }

        /// <summary>
        /// ';' 로 끝나는 선언: C# 파일 범위 네임스페이스, 클래스 안의 함수 선언(C++ 헤더의 프로토타입, C# 추상/인터페이스 멤버),
        /// C# 식 본문 멤버(=&gt;).
        /// </summary>
        private static CodeSymbol ClassifyStatement(List<Token> buffer, Token end, CodeSymbol parent, string separator, SourceLanguage language)
        {
            if (buffer.Count == 0)
            {
                return null;
            }

            int nsIndex = buffer.FindIndex(t => t.Is("namespace"));
            if (language == SourceLanguage.CSharp && nsIndex >= 0 && parent == null)
            {
                string name = ReadQualifiedName(buffer, nsIndex + 1, separator);
                return name == null ? null : CreateSymbol(CodeSymbolKind.Namespace, name, null, buffer[nsIndex].Line, null, separator);
            }

            if (parent == null || parent.Kind != CodeSymbolKind.Type || nsIndex >= 0)
            {
                return null;
            }

            // C# 필드형 이벤트: public event EventHandler Changed;
            if (language == SourceLanguage.CSharp && buffer.Any(t => t.Is("event")) && buffer[buffer.Count - 1].Kind == TokenKind.Identifier)
            {
                Token last = buffer[buffer.Count - 1];
                CodeSymbol evt = CreateSymbol(CodeSymbolKind.Function, last.Text, null, last.Line, parent, separator);
                evt.EndLine = end.Line;
                return evt;
            }

            int arrow = language == SourceLanguage.CSharp ? buffer.FindIndex(t => t.Is("=>")) : -1;
            List<Token> head = arrow > 0 ? buffer.GetRange(0, arrow) : buffer;

            ScanTopLevel(head, language, out int assignIndex, out int typeKeyIndex, out List<int> parens, out int initColonIndex);

            // = 0 (순수 가상), = default, = delete 는 함수 선언으로 본다.
            if (assignIndex >= 0 && !IsSpecialFunctionAssignment(head, assignIndex))
            {
                return null;
            }

            CodeSymbol symbol = null;
            if ((typeKeyIndex < 0 || (parens.Count > 0 && parens[0] < typeKeyIndex)) &&
                TryReadFunction(head, parens, initColonIndex, separator, out string qualified, out string parameters, out int nameLine, out int nameStart))
            {
                // 반환 형식 없이 이름으로 시작하면 매크로 호출(DECLARE_DYNAMIC(Foo))로 본다. 단 생성자 / 소멸자는 허용.
                string bare = qualified.TrimStart('~');
                if (nameStart > 0 || bare == parent.Name || arrow > 0)
                {
                    symbol = CreateSymbol(CodeSymbolKind.Function, qualified, parameters, nameLine, parent, separator);
                }
            }
            else if (arrow > 0 && head.Count >= 2 && head[head.Count - 1].Kind == TokenKind.Identifier)
            {
                // C# 식 본문 속성: public int Count => _count;
                symbol = CreateSymbol(CodeSymbolKind.Function, head[head.Count - 1].Text, null, head[head.Count - 1].Line, parent, separator);
            }

            if (symbol != null)
            {
                symbol.EndLine = end.Line;
            }

            return symbol;
        }

        private static bool IsSpecialFunctionAssignment(List<Token> buffer, int assignIndex)
        {
            if (assignIndex + 1 >= buffer.Count)
            {
                return false;
            }

            Token next = buffer[assignIndex + 1];
            return next.Is("default") || next.Is("delete") || (next.Kind == TokenKind.Literal && next.Text == "0" && assignIndex + 2 == buffer.Count);
        }

        /// <summary>
        /// 괄호 · 각괄호 · 꺾쇠 깊이 0 에서: 대입 '=' 위치, 형식 키워드 위치, '(' 위치들, 생성자 초기화 ':' 위치를 찾는다.
        /// </summary>
        private static void ScanTopLevel(List<Token> buffer, SourceLanguage language, out int assignIndex, out int typeKeyIndex, out List<int> parens, out int initColonIndex)
        {
            assignIndex = -1;
            typeKeyIndex = -1;
            initColonIndex = -1;
            parens = new List<int>();

            HashSet<string> typeKeywords = language == SourceLanguage.CSharp ? CSharpTypeKeywords : CppTypeKeywords;
            int depth = 0;  // ( [
            int angle = 0;  // < >

            for (int i = 0; i < buffer.Count; i++)
            {
                Token t = buffer[i];
                if (t.Kind == TokenKind.Literal)
                {
                    continue;
                }

                switch (t.Text)
                {
                    case "(":
                        if (depth == 0 && angle == 0)
                        {
                            parens.Add(i);
                        }

                        depth++;
                        continue;
                    case "[":
                        depth++;
                        continue;
                    case ")":
                    case "]":
                        depth = Math.Max(0, depth - 1);
                        continue;
                }

                if (depth != 0)
                {
                    continue;
                }

                if (t.Text == "<" && i > 0 && buffer[i - 1].Kind == TokenKind.Identifier && !buffer[i - 1].Is("operator"))
                {
                    angle++;
                }
                else if (t.Text == ">" && angle > 0)
                {
                    angle--;
                }
                else if (angle == 0)
                {
                    if (t.Text == "=" && assignIndex < 0 && !(i > 0 && buffer[i - 1].Is("operator")))
                    {
                        assignIndex = i;
                    }
                    else if (t.Kind == TokenKind.Identifier && typeKeyIndex < 0 && typeKeywords.Contains(t.Text))
                    {
                        typeKeyIndex = i;
                    }
                    else if (t.Text == ":" && initColonIndex < 0 && parens.Count > 0)
                    {
                        initColonIndex = i;
                    }
                }
            }
        }

        #endregion

        #region 이름 읽기

        private static string ReadQualifiedName(List<Token> buffer, int start, string separator)
        {
            var parts = new List<string>();
            for (int i = start; i < buffer.Count; i++)
            {
                Token t = buffer[i];
                if (t.Kind == TokenKind.Identifier)
                {
                    if (t.Text == "inline")
                    {
                        continue;
                    }

                    parts.Add(t.Text);
                }
                else if (!(t.Is("::") || t.Is(".")))
                {
                    break;
                }
            }

            return parts.Count == 0 ? null : string.Join(separator, parts);
        }

        private static string ReadTypeName(List<Token> buffer, int keyIndex, SourceLanguage language)
        {
            int i = keyIndex + 1;

            // enum class Foo, record struct Foo
            if (i < buffer.Count && (buffer[i].Is("class") || buffer[i].Is("struct")))
            {
                i++;
            }

            if (language == SourceLanguage.CSharp)
            {
                return i < buffer.Count && buffer[i].Kind == TokenKind.Identifier ? buffer[i].Text : null;
            }

            // C++: 키워드 뒤 ':' '{' '<' 전까지의 마지막 식별자 (DLL_EXPORT 같은 매크로, [[attr]], __declspec(...) 건너뜀)
            string name = null;
            int depth = 0;
            for (; i < buffer.Count; i++)
            {
                Token t = buffer[i];
                if (t.Is("(") || t.Is("["))
                {
                    depth++;
                    continue;
                }

                if (t.Is(")") || t.Is("]"))
                {
                    depth = Math.Max(0, depth - 1);
                    continue;
                }

                if (depth > 0)
                {
                    continue;
                }

                if (t.Is(":") || t.Is("<"))
                {
                    break;
                }

                if (t.Kind == TokenKind.Identifier && !TypeNameModifiers.Contains(t.Text) && t.Text != "alignas" && t.Text != "__declspec")
                {
                    name = t.Text;
                }
            }

            return name;
        }

        /// <summary>
        /// 함수 이름과 매개변수를 읽는다. 매크로 호출이 앞에 붙은 경우(DECLARE_X(Foo) void f() {})를 위해
        /// 이름처럼 보이는 마지막 '(' 를 매개변수 목록으로 본다. 생성자 초기화 ':' 뒤의 '(' 는 제외한다.
        /// </summary>
        private static bool TryReadFunction(List<Token> buffer, List<int> parens, int initColonIndex, string separator, out string qualifiedName, out string parameters, out int nameLine, out int nameStart)
        {
            qualifiedName = null;
            parameters = null;
            nameLine = 0;
            nameStart = -1;

            for (int p = parens.Count - 1; p >= 0; p--)
            {
                int parenIndex = parens[p];
                if (initColonIndex >= 0 && parenIndex > initColonIndex)
                {
                    continue;
                }

                int nameEnd = parenIndex - 1;
                if (nameEnd < 0)
                {
                    continue;
                }

                // operator() 의 첫 괄호는 이름의 일부
                if (buffer[nameEnd].Is("operator"))
                {
                    continue;
                }

                if (!TryReadNameBackward(buffer, nameEnd, separator, out qualifiedName, out nameStart))
                {
                    continue;
                }

                // 함수 이름 앞이 '->' (후행 반환 형식 안의 decltype 등) 이면 제외
                if (nameStart > 0 && buffer[nameStart - 1].Is("->"))
                {
                    continue;
                }

                parameters = ReadParameters(buffer, parenIndex);
                nameLine = buffer[nameStart].Line;
                return true;
            }

            qualifiedName = null;
            return false;
        }

        private static bool TryReadNameBackward(List<Token> buffer, int end, string separator, out string qualifiedName, out int start)
        {
            qualifiedName = null;
            start = end;

            // operator 오버로드: operator==, operator(), operator new, operator bool ...
            for (int k = end; k >= Math.Max(0, end - 3); k--)
            {
                if (buffer[k].Is("operator"))
                {
                    var op = new StringBuilder("operator");
                    for (int j = k + 1; j <= end; j++)
                    {
                        if (buffer[j].Kind == TokenKind.Identifier)
                        {
                            op.Append(' ');
                        }

                        op.Append(buffer[j].Text);
                    }

                    start = k;
                    qualifiedName = PrependQualifiers(buffer, ref start, op.ToString(), separator);
                    return true;
                }
            }

            int i = end;

            // 제네릭 / 템플릿 인자 건너뛰기: Foo<T>(...)
            if (buffer[i].Is(">"))
            {
                i = SkipAngleBackward(buffer, i);
                if (i < 0)
                {
                    return false;
                }
            }

            Token nameToken = buffer[i];
            if (nameToken.Kind != TokenKind.Identifier || NonFunctionNames.Contains(nameToken.Text))
            {
                return false;
            }

            string name = nameToken.Text;
            start = i;

            // 소멸자 ~Foo
            if (start > 0 && buffer[start - 1].Is("~"))
            {
                start--;
                name = "~" + name;
            }

            qualifiedName = PrependQualifiers(buffer, ref start, name, separator);
            return true;
        }

        /// <summary>Foo::Bar, ns::Foo&lt;T&gt;::Bar, IFoo.Bar (C# 명시적 인터페이스 구현) 의 앞쪽 한정자를 붙인다.</summary>
        private static string PrependQualifiers(List<Token> buffer, ref int start, string name, string separator)
        {
            var parts = new List<string> { name };
            while (start >= 2 && (buffer[start - 1].Is("::") || buffer[start - 1].Is(".")))
            {
                int i = start - 2;
                if (buffer[i].Is(">"))
                {
                    i = SkipAngleBackward(buffer, i);
                    if (i < 0)
                    {
                        break;
                    }
                }

                if (buffer[i].Kind != TokenKind.Identifier)
                {
                    break;
                }

                parts.Insert(0, buffer[i].Text);
                start = i;
            }

            return string.Join(separator, parts);
        }

        /// <summary>'>' 위치에서 짝이 맞는 '&lt;' 바로 앞 토큰 위치를 돌려준다.</summary>
        private static int SkipAngleBackward(List<Token> buffer, int index)
        {
            int depth = 0;
            for (int i = index; i >= 0; i--)
            {
                if (buffer[i].Is(">"))
                {
                    depth++;
                }
                else if (buffer[i].Is("<"))
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i - 1;
                    }
                }
            }

            return -1;
        }

        private static string ReadParameters(List<Token> buffer, int openIndex)
        {
            var builder = new StringBuilder();
            int depth = 0;
            Token previous = default;
            for (int i = openIndex; i < buffer.Count; i++)
            {
                Token t = buffer[i];
                if (t.Is("(") || t.Is("["))
                {
                    depth++;
                    if (depth == 1)
                    {
                        previous = t;
                        continue;
                    }
                }
                else if (t.Is(")") || t.Is("]"))
                {
                    depth--;
                    if (depth == 0)
                    {
                        break;
                    }
                }

                if (builder.Length > 0 && NeedsSpace(previous, t))
                {
                    builder.Append(' ');
                }

                builder.Append(t.Kind == TokenKind.Literal ? "\"...\"" : t.Text);
                previous = t;
            }

            return builder.ToString();
        }

        private static bool NeedsSpace(Token previous, Token current)
        {
            if (current.Is(",") || current.Is(")") || current.Is("]") || current.Is(">") || current.Is("::") || current.Is(".") ||
                previous.Is("(") || previous.Is("[") || previous.Is("<") || previous.Is("::") || previous.Is(".") || previous.Is("~"))
            {
                return false;
            }

            if (current.Is("<") || current.Is("(") || current.Is("["))
            {
                return false;
            }

            if ((current.Is("*") || current.Is("&") || current.Is("&&")) && (previous.Kind == TokenKind.Identifier || previous.Is(">") || previous.Is("*")))
            {
                return false;
            }

            return true;
        }

        #endregion

        private static Frame Declarative(CodeSymbolKind kind, string qualifiedName, string parameters, int line, CodeSymbol parent, string separator) =>
            new Frame { Kind = FrameKind.Declarative, Symbol = CreateSymbol(kind, qualifiedName, parameters, line, parent, separator) };

        private static Frame Body(CodeSymbolKind kind, string qualifiedName, string parameters, int line, CodeSymbol parent, string separator) =>
            new Frame { Kind = FrameKind.Code, Symbol = CreateSymbol(kind, qualifiedName, parameters, line, parent, separator) };

        private static CodeSymbol CreateSymbol(CodeSymbolKind kind, string qualifiedName, string parameters, int line, CodeSymbol parent, string separator)
        {
            // 클래스 안에서 한정된 이름은 C# 명시적 인터페이스 구현(IFoo.Bar)이므로 그대로 둔다.
            // 그 밖(C++ 클래스 밖 정의 Foo::Bar)에서는 마지막 이름만 Name 으로 쓴다.
            int lastSeparator = qualifiedName.LastIndexOf(separator, StringComparison.Ordinal);
            bool keepQualified = parent != null && parent.Kind == CodeSymbolKind.Type;
            string name = lastSeparator >= 0 && !keepQualified && !qualifiedName.StartsWith("operator", StringComparison.Ordinal)
                ? qualifiedName.Substring(lastSeparator + separator.Length)
                : qualifiedName;
            string fullName = parent == null ? qualifiedName : parent.FullName + separator + qualifiedName;

            var symbol = new CodeSymbol(kind, name, fullName, parameters, line, parent, separator);
            symbol.EndLine = int.MinValue; // 닫는 중괄호에서 정해진다.
            return symbol;
        }

        #region 토큰 분리

        private static List<Token> Tokenize(string text, SourceLanguage language)
        {
            var tokens = new List<Token>(text.Length / 6);
            int i = 0;
            int n = text.Length;
            int line = 1;
            bool atLineStart = true;
            int ppDepth = 0;       // #if 중첩 깊이
            int skipDepth = -1;    // #else / #elif 분기를 건너뛰는 중인 깊이

            while (i < n)
            {
                char c = text[i];

                if (c == '\n')
                {
                    line++;
                    i++;
                    atLineStart = true;
                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }

                // 전처리기 줄
                if (c == '#' && atLineStart)
                {
                    i++;
                    while (i < n && (text[i] == ' ' || text[i] == '\t'))
                    {
                        i++;
                    }

                    int wordStart = i;
                    while (i < n && char.IsLetter(text[i]))
                    {
                        i++;
                    }

                    string directive = text.Substring(wordStart, i - wordStart);
                    i = SkipToLineEnd(text, i, language == SourceLanguage.Cpp, ref line);

                    switch (directive)
                    {
                        case "if":
                        case "ifdef":
                        case "ifndef":
                            ppDepth++;
                            break;
                        case "elif":
                        case "elifdef":
                        case "elifndef":
                        case "else":
                            if (skipDepth < 0)
                            {
                                skipDepth = ppDepth;
                            }

                            break;
                        case "endif":
                            if (skipDepth == ppDepth)
                            {
                                skipDepth = -1;
                            }

                            ppDepth = Math.Max(0, ppDepth - 1);
                            break;
                    }

                    continue;
                }

                atLineStart = false;

                // 건너뛰는 #else 분기
                if (skipDepth >= 0)
                {
                    while (i < n && text[i] != '\n')
                    {
                        i++;
                    }

                    continue;
                }

                // 주석
                if (c == '/' && i + 1 < n && text[i + 1] == '/')
                {
                    while (i < n && text[i] != '\n')
                    {
                        i++;
                    }

                    continue;
                }

                if (c == '/' && i + 1 < n && text[i + 1] == '*')
                {
                    i += 2;
                    while (i < n && !(text[i] == '*' && i + 1 < n && text[i + 1] == '/'))
                    {
                        if (text[i] == '\n')
                        {
                            line++;
                        }

                        i++;
                    }

                    i = Math.Min(n, i + 2);
                    continue;
                }

                int tokenStart = i;
                int tokenLine = line;

                // C# 문자열: "..." @"..." $"..." $@"..." """raw"""
                if (language == SourceLanguage.CSharp && (c == '$' || c == '@'))
                {
                    int j = i;
                    bool verbatim = false;
                    while (j < n && (text[j] == '$' || text[j] == '@'))
                    {
                        verbatim |= text[j] == '@';
                        j++;
                    }

                    if (j < n && text[j] == '"')
                    {
                        i = SkipCSharpString(text, j, verbatim, ref line);
                        tokens.Add(new Token { Kind = TokenKind.Literal, Text = "\"\"", Line = tokenLine, Start = tokenStart, End = i });
                        continue;
                    }

                    i++; // @identifier
                    continue;
                }

                if (c == '"')
                {
                    i = language == SourceLanguage.CSharp ? SkipCSharpString(text, i, false, ref line) : SkipQuoted(text, i, '"', ref line);
                    tokens.Add(new Token { Kind = TokenKind.Literal, Text = "\"\"", Line = tokenLine, Start = tokenStart, End = i });
                    continue;
                }

                if (c == '\'')
                {
                    i = SkipQuoted(text, i, '\'', ref line);
                    tokens.Add(new Token { Kind = TokenKind.Literal, Text = "''", Line = tokenLine, Start = tokenStart, End = i });
                    continue;
                }

                // 숫자 (C++14 자릿수 구분자 1'000 포함)
                if (char.IsDigit(c))
                {
                    while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] == '.' || (text[i] == '\'' && language == SourceLanguage.Cpp)))
                    {
                        i++;
                    }

                    tokens.Add(new Token { Kind = TokenKind.Literal, Text = "0", Line = tokenLine, Start = tokenStart, End = i });
                    continue;
                }

                // 식별자
                if (char.IsLetter(c) || c == '_')
                {
                    while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                    {
                        i++;
                    }

                    string word = text.Substring(tokenStart, i - tokenStart);

                    // C++ 문자열 접두어: L"", u8"", R"(...)"
                    if (language == SourceLanguage.Cpp && i < n && (text[i] == '"' || text[i] == '\''))
                    {
                        if (word == "R" || word == "LR" || word == "uR" || word == "UR" || word == "u8R")
                        {
                            i = SkipCppRawString(text, i, ref line);
                            tokens.Add(new Token { Kind = TokenKind.Literal, Text = "\"\"", Line = tokenLine, Start = tokenStart, End = i });
                            continue;
                        }

                        if (word == "L" || word == "u" || word == "U" || word == "u8")
                        {
                            continue; // 다음 반복에서 문자열로 읽는다.
                        }
                    }

                    tokens.Add(new Token { Kind = TokenKind.Identifier, Text = word, Line = tokenLine, Start = tokenStart, End = i });
                    continue;
                }

                // 구두점
                string punct = ReadPunct(text, i);
                i += punct.Length;
                tokens.Add(new Token { Kind = TokenKind.Punct, Text = punct, Line = tokenLine, Start = tokenStart, End = i });
            }

            return tokens;
        }

        private static readonly string[] MultiCharPuncts = { "::", "->", "=>", "==", "!=", "<=", ">=", "&&", "||", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "++", "--" };

        private static string ReadPunct(string text, int i)
        {
            foreach (string p in MultiCharPuncts)
            {
                if (string.CompareOrdinal(text, i, p, 0, p.Length) == 0)
                {
                    return p;
                }
            }

            return text[i].ToString();
        }

        private static int SkipToLineEnd(string text, int i, bool allowContinuation, ref int line)
        {
            int n = text.Length;
            while (i < n)
            {
                char c = text[i];
                if (c == '\n')
                {
                    // 줄 끝의 '\' 는 다음 줄로 이어진다 (C++ 매크로).
                    int k = i - 1;
                    if (k >= 0 && text[k] == '\r')
                    {
                        k--;
                    }

                    if (allowContinuation && k >= 0 && text[k] == '\\')
                    {
                        line++;
                        i++;
                        continue;
                    }

                    return i;
                }

                // 전처리기 줄 끝의 블록 주석이 여러 줄에 걸칠 수 있다.
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    i += 2;
                    while (i < n && !(text[i] == '*' && i + 1 < n && text[i + 1] == '/'))
                    {
                        if (text[i] == '\n')
                        {
                            line++;
                        }

                        i++;
                    }

                    i = Math.Min(n, i + 2);
                    continue;
                }

                i++;
            }

            return i;
        }

        private static int SkipQuoted(string text, int i, char quote, ref int line)
        {
            int n = text.Length;
            i++;
            while (i < n)
            {
                char c = text[i];
                if (c == '\\')
                {
                    i += 2;
                    continue;
                }

                if (c == quote)
                {
                    return i + 1;
                }

                if (c == '\n')
                {
                    return i; // 닫히지 않은 문자열: 줄 끝에서 멈춘다.
                }

                i++;
            }

            return i;
        }

        private static int SkipCSharpString(string text, int i, bool verbatim, ref int line)
        {
            int n = text.Length;

            // 원시 문자열 """ ... """
            int quotes = 0;
            while (i + quotes < n && text[i + quotes] == '"')
            {
                quotes++;
            }

            if (quotes >= 3)
            {
                i += quotes;
                while (i < n)
                {
                    if (text[i] == '\n')
                    {
                        line++;
                    }

                    int run = 0;
                    while (i + run < n && text[i + run] == '"')
                    {
                        run++;
                    }

                    if (run >= quotes)
                    {
                        return i + run;
                    }

                    i += Math.Max(1, run);
                }

                return i;
            }

            if (!verbatim)
            {
                return SkipQuoted(text, i, '"', ref line);
            }

            // @"..." : "" 가 따옴표 이스케이프, 여러 줄 가능
            i++;
            while (i < n)
            {
                if (text[i] == '"')
                {
                    if (i + 1 < n && text[i + 1] == '"')
                    {
                        i += 2;
                        continue;
                    }

                    return i + 1;
                }

                if (text[i] == '\n')
                {
                    line++;
                }

                i++;
            }

            return i;
        }

        /// <summary>R"delim( ... )delim"</summary>
        private static int SkipCppRawString(string text, int i, ref int line)
        {
            int n = text.Length;
            int open = text.IndexOf('(', i);
            if (open < 0)
            {
                return n;
            }

            string terminator = ")" + text.Substring(i + 1, open - i - 1) + "\"";
            int close = text.IndexOf(terminator, open + 1, StringComparison.Ordinal);
            int end = close < 0 ? n : close + terminator.Length;
            for (int k = i; k < end; k++)
            {
                if (text[k] == '\n')
                {
                    line++;
                }
            }

            return end;
        }

        #endregion
    }
}
