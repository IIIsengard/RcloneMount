using System;
using System.Drawing;
using System.Windows.Forms;

namespace RcloneMount.UI
{
    // Forces a TextBox to render with a dark background by intercepting WM_ERASEBKGND
    // and painting the client rect in Theme.SurfaceRaised before the OS Edit control
    // has a chance to blit its default (white) background. Also paints a subtle 1-px
    // border in Theme.BorderEmphasis on WM_NCPAINT so the box still has a visible edge.
    internal sealed class TextBoxDarkChrome : NativeWindow
    {
        private const int WM_ERASEBKGND = 0x0014;
        private readonly TextBox tb;

        private TextBoxDarkChrome(TextBox tb)
        {
            this.tb = tb;
            if (tb.IsHandleCreated) AssignHandle(tb.Handle);
            else tb.HandleCreated += (_, _) => AssignHandle(tb.Handle);
            tb.HandleDestroyed += (_, _) => ReleaseHandle();
        }

        public static void Attach(TextBox tb)
        {
            if (tb.Tag as string == "dark-chrome") return;
            tb.Tag = "dark-chrome";
            var _ = new TextBoxDarkChrome(tb);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_ERASEBKGND)
            {
                try
                {
                    using var g = Graphics.FromHdc(m.WParam);
                    using var bg = new SolidBrush(Theme.SurfaceRaised);
                    g.FillRectangle(bg, tb.ClientRectangle);
                    m.Result = (IntPtr)1;
                    return;
                }
                catch { }
            }
            base.WndProc(ref m);
        }
    }
}
