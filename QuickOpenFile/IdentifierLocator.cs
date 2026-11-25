namespace QuickOpenFile
{
    /// <summary>
    /// 커서가 식별자(변수 · 함수 · 형식 이름) 위나 바로 뒤에 있는지 판단한다.
    /// <para>
    /// 빈 줄, 공백, 기호, <c>//</c> 주석, 문자열 안에서 Quick Info 나 정의 이동 명령을 실행하면
    /// 언어 서비스가 "찾을 수 없음" 으로 Windows 경고음(딩)을 낼 수 있으므로 미리 걸러낸다.
    /// </para>
    /// </summary>
    internal static class IdentifierLocator
    {
        /// <summary>
        /// 줄 안에서 조회할 식별자 문자의 위치(0부터)를 돌려준다. 식별자가 아니면 -1.
        /// </summary>
        /// <param name="lineText">줄 바꿈을 제외한 줄 내용</param>
        /// <param name="column">줄 안에서의 커서 위치 (0부터)</param>
        public static int FindIdentifierColumn(string lineText, int column)
        {
            if (string.IsNullOrEmpty(lineText) || column < 0 || column > lineText.Length)
            {
                return -1;
            }

            int target;
            if (column < lineText.Length && IsIdentifierChar(lineText[column]))
            {
                target = column;
            }
            else if (column > 0 && IsIdentifierChar(lineText[column - 1]))
            {
                target = column - 1; // 식별자 바로 뒤
            }
            else
            {
                return -1;
            }

            return IsInCommentOrString(lineText, target) ? -1 : target;
        }

        private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        /// <summary>줄 앞부분만 보고 <paramref name="index"/> 가 // 주석, /* */ 주석, 문자열 안인지 판단한다.</summary>
        private static bool IsInCommentOrString(string text, int index)
        {
            bool inBlockComment = false;
            char quote = '\0';

            for (int i = 0; i < index; i++)
            {
                char c = text[i];
                char next = i + 1 < text.Length ? text[i + 1] : '\0';

                if (inBlockComment)
                {
                    if (c == '*' && next == '/')
                    {
                        inBlockComment = false;
                        i++;
                    }

                    continue;
                }

                if (quote != '\0')
                {
                    if (c == '\\')
                    {
                        i++; // 이스케이프 문자 건너뛰기
                    }
                    else if (c == quote)
                    {
                        quote = '\0';
                    }

                    continue;
                }

                if (c == '/' && next == '/')
                {
                    return true; // 이후는 모두 줄 주석
                }

                if (c == '/' && next == '*')
                {
                    inBlockComment = true;
                    i++;
                }
                else if (c == '"' || c == '\'')
                {
                    quote = c;
                }
            }

            return inBlockComment || quote != '\0';
        }
    }
}
