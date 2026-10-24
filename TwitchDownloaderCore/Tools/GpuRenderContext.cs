using SkiaSharp;
using System;
using System.Runtime.InteropServices;

namespace TwitchDownloaderCore.Tools
{
    /// <summary>
    /// Optional GPU-accelerated rendering context for the danmaku renderer,
    /// built on SkiaSharp's GPU backend (Ganesh over OpenGL).
    ///
    /// A headless OpenGL context is created per platform (WGL with an
    /// invisible window on Windows, EGL surfaceless on Linux), so neither
    /// the CLI nor the GUI needs a visible window. Frames are drawn on a
    /// GPU-backed <see cref="SKSurface"/> and read back into a CPU bitmap,
    /// which keeps the rest of the pipeline (PNG encoding, the ffmpeg raw
    /// frame pipe) unchanged.
    ///
    /// GPU rendering is strictly opt-in and best-effort: any failure while
    /// creating the context or drawing a frame makes the caller fall back
    /// to the regular CPU renderer for the rest of the operation.
    /// </summary>
    public sealed class GpuRenderContext : IDisposable
    {
        private const uint GL_RENDERER = 0x1F01;

        private readonly IGlContext _glContext;
        private readonly GRGlInterface _glInterface;
        private readonly GRContext _grContext;
        private readonly object _lock = new();
        private SKSurface _surface;
        private SKImageInfo _surfaceInfo;

        public string RendererName { get; }

        private GpuRenderContext(IGlContext glContext, GRGlInterface glInterface, GRContext grContext, string rendererName)
        {
            _glContext = glContext;
            _glInterface = glInterface;
            _grContext = grContext;
            RendererName = rendererName;
        }

        /// <summary>
        /// Creates a GPU context, or returns null (with the reason in
        /// <paramref name="failureReason"/>) when no usable GPU/OpenGL
        /// stack is available on this machine.
        /// </summary>
        public static GpuRenderContext TryCreate(out string failureReason)
        {
            failureReason = null;
            IGlContext gl = null;
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    gl = WglContext.TryCreate(out failureReason);
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    gl = EglContext.TryCreate(out failureReason);
                }
                else
                {
                    failureReason = "GPU rendering is only supported on Windows and Linux";
                }

                if (gl is null)
                {
                    failureReason ??= "no OpenGL context available";
                    return null;
                }

                if (!gl.MakeCurrent())
                {
                    failureReason = "could not make the OpenGL context current";
                    gl.Dispose();
                    return null;
                }

                var glInterface = GRGlInterface.CreateOpenGl(name => gl.GetProcAddress(name));
                if (glInterface is null)
                {
                    failureReason = "Skia could not assemble an OpenGL interface";
                    gl.Dispose();
                    return null;
                }

                var grContext = GRContext.CreateGl(glInterface);
                if (grContext is null)
                {
                    failureReason = "Skia could not create a GPU context";
                    glInterface.Dispose();
                    gl.Dispose();
                    return null;
                }

                string rendererName = gl.GetString(GL_RENDERER) ?? "unknown GPU";
                return new GpuRenderContext(gl, glInterface, grContext, rendererName);
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                gl?.Dispose();
                return null;
            }
        }

        /// <summary>
        /// Draws one frame on the GPU surface and reads the pixels back
        /// into <paramref name="target"/> (whose size and format the
        /// caller already uses for the CPU path). Returns false when the
        /// GPU path failed; exceptions from the draw callback propagate.
        /// </summary>
        public bool RenderFrame(int width, int height, Action<SKCanvas> draw, SKBitmap target)
        {
            lock (_lock)
            {
                if (!_glContext.MakeCurrent())
                {
                    return false;
                }

                var surface = GetSurface(width, height);
                if (surface is null)
                {
                    return false;
                }

                draw(surface.Canvas);
                surface.Canvas.Flush();
                using var image = surface.Snapshot();
                if (image is null)
                {
                    return false;
                }

                return image.ReadPixels(target.Info, target.GetPixels(), target.RowBytes, 0, 0);
            }
        }

        /// <summary>
        /// Draws one frame on the GPU surface and returns a GPU-backed
        /// snapshot of it (the caller disposes the image), or null when
        /// the GPU path failed. Exceptions from the draw callback
        /// propagate.
        /// </summary>
        public SKImage RenderImage(int width, int height, Action<SKCanvas> draw)
        {
            lock (_lock)
            {
                if (!_glContext.MakeCurrent())
                {
                    return null;
                }

                var surface = GetSurface(width, height);
                if (surface is null)
                {
                    return null;
                }

                draw(surface.Canvas);
                surface.Canvas.Flush();
                return surface.Snapshot();
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _glContext.MakeCurrent();
                _surface?.Dispose();
                _surface = null;
                _grContext.Dispose();
                _glInterface.Dispose();
                _glContext.Dispose();
            }
        }

        private SKSurface GetSurface(int width, int height)
        {
            if (_surface is not null && _surfaceInfo.Width == width && _surfaceInfo.Height == height)
            {
                return _surface;
            }

            _surface?.Dispose();
            // Rgba8888 is renderable on every GL stack; the readback into
            // the caller's Bgra8888 bitmap converts the channel order.
            _surfaceInfo = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            _surface = SKSurface.Create(_grContext, true, _surfaceInfo);
            return _surface;
        }

        private interface IGlContext : IDisposable
        {
            bool MakeCurrent();
            IntPtr GetProcAddress(string name);
            string GetString(uint name);
        }

        /// <summary>
        /// Headless WGL context (Windows): an invisible top-level window
        /// provides the device context; Skia renders into its own
        /// offscreen GPU surface, so the window is never shown.
        /// </summary>
        private sealed class WglContext : IGlContext
        {
            private static readonly object ClassLock = new();
            private static bool _classRegistered;
            private static WndProcDelegate _wndProc; // kept alive for the process lifetime

            private IntPtr _hwnd;
            private IntPtr _dc;
            private IntPtr _context;
            private IntPtr _opengl32;

            public static WglContext TryCreate(out string failureReason)
            {
                failureReason = null;
                var gl = new WglContext();
                try
                {
                    IntPtr hInstance = GetModuleHandle(null);
                    if (!EnsureWindowClass(hInstance))
                    {
                        failureReason = "RegisterClassEx failed";
                        return null;
                    }

                    gl._hwnd = CreateWindowEx(0, "TwitchDownloaderGpuContext", "gpu", 0, -100, -100, 1, 1, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
                    if (gl._hwnd == IntPtr.Zero)
                    {
                        failureReason = "CreateWindowEx failed";
                        return null;
                    }

                    gl._dc = GetDC(gl._hwnd);
                    if (gl._dc == IntPtr.Zero)
                    {
                        failureReason = "GetDC failed";
                        gl.Dispose();
                        return null;
                    }

                    var pfd = new PixelFormatDescriptor
                    {
                        nSize = (ushort)Marshal.SizeOf<PixelFormatDescriptor>(),
                        nVersion = 1,
                        dwFlags = 0x04 /* PFD_DRAW_TO_WINDOW */ | 0x20 /* PFD_SUPPORT_OPENGL */,
                        iPixelType = 0 /* PFD_TYPE_RGBA */,
                        cColorBits = 32,
                        cAlphaBits = 8,
                        cDepthBits = 24,
                        cStencilBits = 8,
                        iLayerType = 0 /* PFD_MAIN_PLANE */,
                    };
                    int format = ChoosePixelFormat(gl._dc, ref pfd);
                    if (format == 0 || !SetPixelFormat(gl._dc, format, ref pfd))
                    {
                        failureReason = "no OpenGL-capable pixel format";
                        gl.Dispose();
                        return null;
                    }

                    gl._context = wglCreateContext(gl._dc);
                    if (gl._context == IntPtr.Zero)
                    {
                        failureReason = "wglCreateContext failed";
                        gl.Dispose();
                        return null;
                    }

                    return gl;
                }
                catch (Exception ex)
                {
                    failureReason = ex.Message;
                    gl.Dispose();
                    return null;
                }
            }

            private static bool EnsureWindowClass(IntPtr hInstance)
            {
                lock (ClassLock)
                {
                    if (_classRegistered)
                    {
                        return true;
                    }

                    _wndProc = DefWindowProc;
                    var wc = new WindowClassEx
                    {
                        cbSize = (uint)Marshal.SizeOf<WindowClassEx>(),
                        lpfnWndProc = _wndProc,
                        hInstance = hInstance,
                        lpszClassName = "TwitchDownloaderGpuContext",
                    };
                    // A zero result with ERROR_CLASS_ALREADY_EXISTS (1410)
                    // is fine too; any other failure surfaces later when
                    // creating the window.
                    _classRegistered = RegisterClassEx(ref wc) != 0 || Marshal.GetLastWin32Error() == 1410;
                    return _classRegistered;
                }
            }

            public bool MakeCurrent() => wglMakeCurrent(_dc, _context);

            public IntPtr GetProcAddress(string name)
            {
                var address = wglGetProcAddress(name);
                // wglGetProcAddress cannot resolve core OpenGL 1.1 entry
                // points (it returns null or a small sentinel value);
                // those live as plain exports of opengl32.dll.
                if (address == IntPtr.Zero || address == (IntPtr)1 || address == (IntPtr)2 || address == (IntPtr)3 || address == (IntPtr)(-1))
                {
                    _opengl32 = _opengl32 == IntPtr.Zero ? GetModuleHandle("opengl32.dll") : _opengl32;
                    address = _opengl32 == IntPtr.Zero ? IntPtr.Zero : KernelGetProcAddress(_opengl32, name);
                }

                return address;
            }

            public string GetString(uint name)
            {
                var ptr = glGetString(name);
                return ptr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(ptr);
            }

            public void Dispose()
            {
                if (_context != IntPtr.Zero)
                {
                    wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
                    wglDeleteContext(_context);
                    _context = IntPtr.Zero;
                }

                if (_hwnd != IntPtr.Zero)
                {
                    if (_dc != IntPtr.Zero)
                    {
                        ReleaseDC(_hwnd, _dc);
                        _dc = IntPtr.Zero;
                    }

                    DestroyWindow(_hwnd);
                    _hwnd = IntPtr.Zero;
                }
            }

            private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            private struct WindowClassEx
            {
                public uint cbSize;
                public uint style;
                public WndProcDelegate lpfnWndProc;
                public int cbClsExtra;
                public int cbWndExtra;
                public IntPtr hInstance;
                public IntPtr hIcon;
                public IntPtr hCursor;
                public IntPtr hbrBackground;
                public string lpszMenuName;
                public string lpszClassName;
                public IntPtr hIconSm;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct PixelFormatDescriptor
            {
                public ushort nSize;
                public ushort nVersion;
                public uint dwFlags;
                public byte iPixelType;
                public byte cColorBits;
                public byte cRedBits;
                public byte cRedShift;
                public byte cGreenBits;
                public byte cGreenShift;
                public byte cBlueBits;
                public byte cBlueShift;
                public byte cAlphaBits;
                public byte cAlphaShift;
                public byte cAccumBits;
                public byte cAccumRedBits;
                public byte cAccumGreenBits;
                public byte cAccumBlueBits;
                public byte cAccumAlphaBits;
                public byte cDepthBits;
                public byte cStencilBits;
                public byte cAuxBuffers;
                public byte iLayerType;
                public byte bReserved;
                public uint dwLayerMask;
                public uint dwVisibleMask;
                public uint dwDamageMask;
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern IntPtr GetModuleHandle(string moduleName);

            [DllImport("kernel32.dll", EntryPoint = "GetProcAddress", CharSet = CharSet.Ansi, SetLastError = true)]
            private static extern IntPtr KernelGetProcAddress(IntPtr module, string procName);

            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern ushort RegisterClassEx(ref WindowClassEx windowClass);

            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

            [DllImport("user32.dll")]
            private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll")]
            private static extern IntPtr GetDC(IntPtr hWnd);

            [DllImport("user32.dll")]
            private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool DestroyWindow(IntPtr hWnd);

            [DllImport("gdi32.dll", SetLastError = true)]
            private static extern int ChoosePixelFormat(IntPtr hdc, ref PixelFormatDescriptor pfd);

            [DllImport("gdi32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool SetPixelFormat(IntPtr hdc, int format, ref PixelFormatDescriptor pfd);

            [DllImport("opengl32.dll", SetLastError = true)]
            private static extern IntPtr wglCreateContext(IntPtr hdc);

            [DllImport("opengl32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool wglMakeCurrent(IntPtr hdc, IntPtr hglrc);

            [DllImport("opengl32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool wglDeleteContext(IntPtr hglrc);

            [DllImport("opengl32.dll", CharSet = CharSet.Ansi)]
            private static extern IntPtr wglGetProcAddress(string procName);

            [DllImport("opengl32.dll")]
            private static extern IntPtr glGetString(uint name);
        }

        /// <summary>
        /// Headless EGL context (Linux): the surfaceless platform needs
        /// no display server or window at all.
        /// </summary>
        private sealed class EglContext : IGlContext
        {
            private const uint EGL_PLATFORM_SURFACELESS_MESA = 0x31DD;
            private const uint EGL_OPENGL_API = 0x30A2;
            private const int EGL_NONE = 0x3038;

            private IntPtr _display;
            private IntPtr _context;

            public static EglContext TryCreate(out string failureReason)
            {
                failureReason = null;
                var gl = new EglContext();
                try
                {
                    gl._display = eglGetPlatformDisplay(EGL_PLATFORM_SURFACELESS_MESA, IntPtr.Zero, null);
                    if (gl._display == IntPtr.Zero)
                    {
                        gl._display = eglGetDisplay(IntPtr.Zero);
                    }

                    if (gl._display == IntPtr.Zero)
                    {
                        failureReason = "no EGL display";
                        return null;
                    }

                    if (!eglInitialize(gl._display, out _, out _))
                    {
                        failureReason = $"eglInitialize failed (error {eglGetError():X})";
                        gl._display = IntPtr.Zero;
                        return null;
                    }

                    // EGL_SURFACE_TYPE=EGL_PBUFFER_BIT, EGL_RENDERABLE_TYPE=EGL_OPENGL_BIT,
                    // 8 bits per channel.
                    int[] configAttribs = { 0x3033, 0x0001, 0x3040, 0x0008, 0x3024, 8, 0x3023, 8, 0x3022, 8, 0x3021, 8, EGL_NONE };
                    var configs = new IntPtr[1];
                    if (!eglChooseConfig(gl._display, configAttribs, configs, 1, out int configCount) || configCount == 0)
                    {
                        failureReason = $"eglChooseConfig failed (error {eglGetError():X})";
                        gl.Dispose();
                        return null;
                    }

                    eglBindAPI(EGL_OPENGL_API);
                    gl._context = eglCreateContext(gl._display, configs[0], IntPtr.Zero, new[] { EGL_NONE });
                    if (gl._context == IntPtr.Zero)
                    {
                        failureReason = $"eglCreateContext failed (error {eglGetError():X})";
                        gl.Dispose();
                        return null;
                    }

                    return gl;
                }
                catch (Exception ex)
                {
                    failureReason = ex.Message;
                    gl.Dispose();
                    return null;
                }
            }

            public bool MakeCurrent() => eglMakeCurrent(_display, IntPtr.Zero, IntPtr.Zero, _context);

            public IntPtr GetProcAddress(string name) => eglGetProcAddress(name);

            public string GetString(uint name)
            {
                var ptr = glGetString(name);
                return ptr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(ptr);
            }

            public void Dispose()
            {
                if (_display != IntPtr.Zero)
                {
                    eglMakeCurrent(_display, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    if (_context != IntPtr.Zero)
                    {
                        eglDestroyContext(_display, _context);
                        _context = IntPtr.Zero;
                    }

                    eglTerminate(_display);
                    _display = IntPtr.Zero;
                }
            }

            [DllImport("libEGL.so.1")]
            private static extern IntPtr eglGetPlatformDisplay(uint platform, IntPtr nativeDisplay, int[] attribs);

            [DllImport("libEGL.so.1")]
            private static extern IntPtr eglGetDisplay(IntPtr nativeDisplay);

            [DllImport("libEGL.so.1")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool eglInitialize(IntPtr display, out int major, out int minor);

            [DllImport("libEGL.so.1")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool eglChooseConfig(IntPtr display, int[] attribs, IntPtr[] configs, int configSize, out int numConfigs);

            [DllImport("libEGL.so.1")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool eglBindAPI(uint api);

            [DllImport("libEGL.so.1")]
            private static extern IntPtr eglCreateContext(IntPtr display, IntPtr config, IntPtr shareContext, int[] attribs);

            [DllImport("libEGL.so.1")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool eglMakeCurrent(IntPtr display, IntPtr draw, IntPtr read, IntPtr context);

            [DllImport("libEGL.so.1")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool eglDestroyContext(IntPtr display, IntPtr context);

            [DllImport("libEGL.so.1")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool eglTerminate(IntPtr display);

            [DllImport("libEGL.so.1")]
            private static extern uint eglGetError();

            [DllImport("libEGL.so.1", CharSet = CharSet.Ansi)]
            private static extern IntPtr eglGetProcAddress(string procName);

            [DllImport("libGL.so.1")]
            private static extern IntPtr glGetString(uint name);
        }
    }
}
