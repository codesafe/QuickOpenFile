using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;

namespace QuickOpenFile.NavigationBar
{
    /// <summary>
    /// 커서 위치 심볼의 선언(예: <c>int m_count</c>)을 VS Quick Info(마우스 툴팁) 에서 가져온다.
    /// 툴팁 UI 는 띄우지 않는다.
    /// </summary>
    internal static class QuickInfoReader
    {
        // 진단 메시지(오류/경고) 툴팁은 선언이 아니므로 건너뛴다. 예: "E0020 ...", "C4100 ...", "lnt-..."
        // C#: "CS0103 ...", "IDE0051 ...", "CA1822 ..." 등
        private static readonly Regex DiagnosticPattern = new Regex(@"^(E\d{4}|C\d{4,5}|lnt-|VCR\d+|CS\d{4}|IDE\d{4}|CA\d{4}|warning |error )", RegexOptions.Compiled);

        // C# 툴팁 앞의 분류 표시: "(local variable) int count" → "int count" (Visual Assist 와 같은 표시)
        private static readonly Regex KindPrefixPattern = new Regex(@"^(\([a-z][a-z ]*\)\s+)+", RegexOptions.Compiled);

        public static async Task<string> GetDefinitionAsync(IAsyncQuickInfoBroker broker, ITextView view, SnapshotPoint point, CancellationToken cancellationToken)
        {
            string text = await QueryAsync(broker, view, point, cancellationToken);

            // 커서가 식별자 바로 뒤에 있으면 한 글자 앞에서 다시 시도한다.
            if (text == null && point.Position > 0)
            {
                text = await QueryAsync(broker, view, point - 1, cancellationToken);
            }

            return text;
        }

        private static async Task<string> QueryAsync(IAsyncQuickInfoBroker broker, ITextView view, SnapshotPoint point, CancellationToken cancellationToken)
        {
            ITrackingPoint trackingPoint = point.Snapshot.CreateTrackingPoint(point.Position, PointTrackingMode.Positive);
            QuickInfoItemsCollection result = await broker.GetQuickInfoItemsAsync(view, trackingPoint, cancellationToken);
            if (result?.Items == null)
            {
                return null;
            }

            foreach (object item in result.Items)
            {
                string line = FirstLine(Flatten(item));
                if (!string.IsNullOrWhiteSpace(line) && !DiagnosticPattern.IsMatch(line))
                {
                    string declaration = KindPrefixPattern.Replace(line, string.Empty);
                    return declaration.Length > 0 ? declaration : line;
                }
            }

            return null;
        }

        private static string Flatten(object item)
        {
            var builder = new StringBuilder();
            Append(item, builder);
            return builder.ToString();
        }

        private static void Append(object item, StringBuilder builder)
        {
            switch (item)
            {
                case null:
                    return;
                case string s:
                    builder.Append(s);
                    break;
                case ClassifiedTextElement classified:
                    foreach (ClassifiedTextRun run in classified.Runs)
                    {
                        builder.Append(run.Text);
                    }

                    break;
                case ContainerElement container:
                    bool stacked = container.Style.HasFlag(ContainerElementStyle.Stacked);
                    foreach (object child in container.Elements)
                    {
                        Append(child, builder);
                        if (stacked)
                        {
                            builder.Append('\n');
                        }
                    }

                    break;
                case TextBlock textBlock:
                    builder.Append(textBlock.Text);
                    break;
                case IEnumerable<object> list:
                    foreach (object child in list)
                    {
                        Append(child, builder);
                        builder.Append('\n');
                    }

                    break;
            }
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            foreach (string raw in text.Split('\n'))
            {
                string line = Regex.Replace(raw, @"\s+", " ").Trim();
                if (line.Length > 0)
                {
                    return line;
                }
            }

            return null;
        }
    }
}
