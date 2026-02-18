using System;
using System.IO;
using System.Text;

namespace Obj.Utils
{
    public sealed class ReturnCapture : IDisposable
    {
        private readonly TextWriter _original;
        private readonly StringWriter _buffer;
        private bool _disposed;

        public ReturnCapture()
        {
            _original = Console.Out;
            _buffer = new StringWriter(new StringBuilder());
            Console.SetOut(_buffer);
        }

        public string GetText()
        {
            return _buffer.ToString();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Console.SetOut(_original);
        }
    }
}
