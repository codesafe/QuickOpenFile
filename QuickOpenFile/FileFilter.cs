using System;
using System.Collections.Generic;
using System.Linq;

namespace QuickOpenFile
{
    /// <summary>
    /// Visual Assist "Open File in Solution" 와 같은 방식의 필터.
    /// <list type="bullet">
    /// <item>공백으로 구분된 여러 키워드는 모두 일치해야 한다 (AND).</item>
    /// <item>'-' 로 시작하는 키워드는 제외 조건이다. (예: <c>render -test</c>)</item>
    /// <item>'\' 또는 '/' 를 포함한 키워드는 상대 경로 전체와 비교하고, 그 외에는 파일 이름과 비교한다.</item>
    /// </list>
    /// </summary>
    public static class FileFilter
    {
        private sealed class Token
        {
            public string Text;
            public bool Exclude;
            public bool MatchPath;
        }

        public static List<SolutionFileEntry> Apply(IReadOnlyList<SolutionFileEntry> files, string filterText)
        {
            List<Token> tokens = Parse(filterText);
            if (tokens.Count == 0)
            {
                return files
                    .OrderBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            // 순위 계산에 사용할 첫 번째 "파일 이름" 키워드
            Token primary = tokens.FirstOrDefault(t => !t.Exclude && !t.MatchPath);

            var matched = new List<KeyValuePair<int, SolutionFileEntry>>();
            foreach (SolutionFileEntry file in files)
            {
                if (!IsMatch(file, tokens))
                {
                    continue;
                }

                matched.Add(new KeyValuePair<int, SolutionFileEntry>(Score(file, primary), file));
            }

            return matched
                .OrderBy(p => p.Key)
                .ThenBy(p => p.Value.FileName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.Value.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Select(p => p.Value)
                .ToList();
        }

        private static List<Token> Parse(string filterText)
        {
            var tokens = new List<Token>();
            if (string.IsNullOrWhiteSpace(filterText))
            {
                return tokens;
            }

            foreach (string raw in filterText.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                bool exclude = raw.Length > 1 && raw[0] == '-';
                string text = (exclude ? raw.Substring(1) : raw).Replace('/', '\\');
                if (text.Length == 0)
                {
                    continue;
                }

                tokens.Add(new Token
                {
                    Text = text,
                    Exclude = exclude,
                    MatchPath = text.IndexOf('\\') >= 0,
                });
            }

            return tokens;
        }

        private static bool IsMatch(SolutionFileEntry file, List<Token> tokens)
        {
            foreach (Token token in tokens)
            {
                string target = token.MatchPath ? file.RelativePath : file.FileName;
                bool found = target.IndexOf(token.Text, StringComparison.OrdinalIgnoreCase) >= 0;
                if (found == token.Exclude)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>작을수록 위에 표시된다.</summary>
        private static int Score(SolutionFileEntry file, Token primary)
        {
            if (primary == null)
            {
                return 0;
            }

            string key = primary.Text;
            if (string.Equals(file.FileName, key, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(file.NameWithoutExtension, key, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            if (file.FileName.StartsWith(key, StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            return 2;
        }
    }
}
