// Minimal test runner for the launcher and setup tests (C# 5, no frameworks): every public static void method whose
// name starts with "Test" is a test. Build and run: powershell -File ops\build-setup.ps1 (it runs both test exes).
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace WolfTeamDirilis.Tests
{
    internal sealed class TestFailure : Exception
    {
        public TestFailure(string m) : base(m) { }
    }

    internal static class Assert
    {
        public static void True(bool c, string what)
        {
            if (!c) throw new TestFailure("expected true: " + what);
        }

        public static void False(bool c, string what)
        {
            if (c) throw new TestFailure("expected false: " + what);
        }

        public static void Equal<T>(T expected, T actual, string what)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new TestFailure(what + ": expected <" + expected + "> got <" + actual + ">");
        }

        public static TE Throws<TE>(Action a, string what) where TE : Exception
        {
            try { a(); }
            catch (TE e) { return e; }
            catch (Exception e) { throw new TestFailure(what + ": expected " + typeof(TE).Name + " got " + e.GetType().Name + ": " + e.Message); }
            throw new TestFailure(what + ": expected " + typeof(TE).Name + ", nothing thrown");
        }
    }

    internal static class Runner
    {
        public static int Run(Type suite)
        {
            int pass = 0, fail = 0;
            foreach (MethodInfo m in suite.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (!m.Name.StartsWith("Test") || m.GetParameters().Length != 0) continue;
                try
                {
                    m.Invoke(null, null);
                    pass++;
                    Console.WriteLine("PASS " + m.Name);
                }
                catch (TargetInvocationException e)
                {
                    fail++;
                    Console.WriteLine("FAIL " + m.Name + ": " + e.InnerException.GetType().Name + ": " + e.InnerException.Message);
                }
            }
            Console.WriteLine(suite.Name + ": " + pass + " passed, " + fail + " failed");
            return fail == 0 ? 0 : 1;
        }

        public static string TempDir(string label)
        {
            // A space and Turkish letters on purpose: install and game paths must work with them.
            string d = Path.Combine(Path.GetTempPath(), "wtd-test " + label + " Ğüşİ " + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(d);
            return d;
        }

        /// <summary>Creates the window handles of a form and all its children without showing anything, so that
        /// DrawToBitmap can print them (a never-shown form creates only its own handle).</summary>
        public static void CreateHandles(System.Windows.Forms.Control c)
        {
            if (c.Handle == IntPtr.Zero) throw new InvalidOperationException("no handle");
            foreach (System.Windows.Forms.Control child in c.Controls) CreateHandles(child);
        }

        /// <summary>DrawToBitmap of a never-shown form, with its direct children painted again on top in z-order
        /// (DrawToBitmap alone prints children in reverse z-order and skips some that were made visible later).</summary>
        public static System.Drawing.Bitmap Snapshot(System.Windows.Forms.Form f)
        {
            CreateHandles(f);
            // Whole window (a dialog has a title bar); children are placed at the client area's offset.
            System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(f.Width, f.Height);
            f.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height));
            int ox = (f.Width - f.ClientSize.Width) / 2, oy = f.Height - f.ClientSize.Height - ox;
            System.Reflection.MethodInfo getState = typeof(System.Windows.Forms.Control).GetMethod("GetState",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(bmp))
            {
                for (int i = f.Controls.Count - 1; i >= 0; i--)
                {
                    System.Windows.Forms.Control c = f.Controls[i];
                    if (!(bool)getState.Invoke(c, new object[] { 2 }) || c.Width <= 0 || c.Height <= 0) continue; // 2 = STATE_VISIBLE
                    using (System.Drawing.Bitmap cb = new System.Drawing.Bitmap(c.Width, c.Height))
                    {
                        c.DrawToBitmap(cb, new System.Drawing.Rectangle(0, 0, c.Width, c.Height));
                        g.DrawImageUnscaled(cb, c.Left + ox, c.Top + oy);
                    }
                }
            }
            return bmp;
        }

        public static void DeleteTree(string d)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
