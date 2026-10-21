using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UcanView.Helpers;

namespace UcanView.ViewModels
{
    /// <summary>One line of a converted trace shown in the output list.</summary>
    public sealed class TraceLine
    {
        private readonly TraceLineList _owner;

        internal TraceLine(TraceLineList owner, int index, string text)
        {
            _owner = owner;
            Index = index;
            Text = text;
        }

        public int Index { get; }
        public string Text { get; }

        internal bool BelongsTo(TraceLineList list) => ReferenceEquals(_owner, list);

        // Lines are created on demand, so the same line read twice is two objects; equality by
        // position keeps a selected line selected when its row is regenerated.
        public override bool Equals(object obj) => obj is TraceLine o && ReferenceEquals(o._owner, _owner) && o.Index == Index;
        public override int GetHashCode() => Index;
        public override string ToString() => Text;
    }

    /// <summary>
    /// The text of a converted binary trace, formatted a line at a time as the list scrolls.
    /// A trace of millions of messages converts to hundreds of megabytes of text; holding it in
    /// a TextBox hung the UI and ran this 32-bit process out of memory. Message records have a
    /// fixed size, so any line is read straight from the trace file.
    /// </summary>
    /// <remarks>Read-only, and used from the UI thread only.</remarks>
    public sealed class TraceLineList : IList, IDisposable
    {
        private const int CacheLimit = 4096;

        private readonly UcanTraceFile _trace;
        private readonly bool _differentialTime;
        private readonly bool _highRes;
        private readonly string[] _headLines;
        private readonly string[] _tailLines;
        private readonly int _messageCount;
        private readonly Dictionary<int, TraceLine> _cache = new Dictionary<int, TraceLine>();
        private readonly StringBuilder _line = new StringBuilder(96);

        public TraceLineList(string tracePath, string headerText, bool differentialTime)
        {
            _trace = UcanTraceFile.Open(tracePath);
            _differentialTime = differentialTime;
            _highRes = _trace.IsHighResTimer;

            // An ItemsControl indexes with int; a capture past that is not realistic.
            _messageCount = (int)Math.Min(_trace.MessageCount, int.MaxValue - 64);

            // The same lines the converted text file has around its messages.
            var head = new List<string>(SplitLines(headerText));
            head.Add(string.Empty);
            head.AddRange(SplitLines(UcanMsgFormatProvider.GetMessageHeader()));
            _headLines = head.ToArray();
            _tailLines = new[] { string.Empty, $"Total number of CAN messages: {_messageCount}" };

            TracePath = tracePath;
        }

        public string TracePath { get; }
        public int MessageCount => _messageCount;
        public int Count => _headLines.Length + _messageCount + _tailLines.Length;

        public object this[int index]
        {
            get
            {
                if (index < 0 || index >= Count)
                    throw new ArgumentOutOfRangeException(nameof(index));

                if (_cache.TryGetValue(index, out var line))
                    return line;

                if (_cache.Count >= CacheLimit)
                    _cache.Clear();

                line = new TraceLine(this, index, ReadLine(index));
                _cache[index] = line;
                return line;
            }
            set => throw new NotSupportedException();
        }

        /// <summary>The text of a line.</summary>
        public string GetText(int index) => ((TraceLine)this[index]).Text;

        private string ReadLine(int index)
        {
            if (index < _headLines.Length)
                return _headLines[index];

            int message = index - _headLines.Length;
            if (message >= _messageCount)
                return _tailLines[message - _messageCount];

            var msg = _trace.ReadMessageAt(message);
            if (_differentialTime && message > 0)
                msg.m_dwTime -= _trace.ReadMessageAt(message - 1).m_dwTime;

            _line.Clear();
            UcanMsgFormatProvider.AppendMessage(_line, msg, _highRes);
            return _line.ToString();
        }

        private static IEnumerable<string> SplitLines(string text) =>
            (text ?? string.Empty).TrimEnd('\r', '\n').Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

        // Constant time: WPF looks items up by IndexOf when selecting, and a scan would read
        // the whole trace.
        public int IndexOf(object value) =>
            value is TraceLine line && line.BelongsTo(this) ? line.Index : -1;

        public bool Contains(object value) => IndexOf(value) >= 0;

        public IEnumerator GetEnumerator()
        {
            for (int i = 0; i < Count; i++)
                yield return this[i];
        }

        public void CopyTo(Array array, int index)
        {
            for (int i = 0; i < Count; i++)
                array.SetValue(this[i], index + i);
        }

        public bool IsReadOnly => true;
        public bool IsFixedSize => true;
        public bool IsSynchronized => false;
        public object SyncRoot => this;

        public int Add(object value) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public void Insert(int index, object value) => throw new NotSupportedException();
        public void Remove(object value) => throw new NotSupportedException();
        public void RemoveAt(int index) => throw new NotSupportedException();

        public void Dispose() => _trace.Dispose();
    }
}
