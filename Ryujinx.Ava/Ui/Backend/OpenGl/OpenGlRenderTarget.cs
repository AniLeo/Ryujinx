using System;
using System.Threading;
using Avalonia.Skia;
using OpenTK.Graphics.OpenGL;
using Ryujinx.Ava.Ui.Backend.OpenGl;
using SkiaSharp;

namespace Ryujinx.Ava.Ui.Backend.OpenGl
{
    internal class OpenGlRenderTarget : ISkiaGpuRenderTarget
    {
        internal GRContext GrContext { get; set; }
        private readonly OpenGlSurface _openglSurface;

        public OpenGlRenderTarget(OpenGlSurface openglSurface)
        {
            _openglSurface = openglSurface;
        }

        public void Dispose()
        {
           // _renderTarget.Dispose();
        }

        public ISkiaGpuRenderSession BeginRenderingSession()
        {
            var session = _openglSurface.BeginDraw();
            bool success = false;
            try
            {
                var size = session.Size;
                var scaling = session.Scaling;
                if (size.Width <= 0 || size.Height <= 0 || scaling < 0)
                {
                    session.Dispose();
                    throw new InvalidOperationException(
                        $"Can't create drawing context for surface with {size} size and {scaling} scaling");
                }

                lock (GrContext)
                {
                    GrContext.ResetContext();

                    var imageInfo = new GRGlFramebufferInfo()
                    {
                        FramebufferObjectId = 0,
                        Format = (uint)InternalFormat.Rgba8
                    };

                    var stencils = GL.GetInteger(GetPName.StencilBits);

                    var renderTarget =
                        new GRBackendRenderTarget((int)size.Width, (int)size.Height, 1, stencils, imageInfo);
                    var surface = SKSurface.Create(GrContext, renderTarget,
                        session.IsYFlipped ? GRSurfaceOrigin.TopLeft : GRSurfaceOrigin.BottomLeft,
                        SKColorType.Rgba8888, SKColorSpace.CreateSrgb());

                    if (surface == null)
                        throw new InvalidOperationException(
                            $"Surface can't be created with the provided render target");

                    success = true;

                    return new OpenGlGpuSession(GrContext, renderTarget, surface, session);
                }
            }
            finally
            {
                if (!success)
                    session.Dispose();
            }
        }

        public bool IsCorrupted { get; }

        internal class OpenGlGpuSession : ISkiaGpuRenderSession
        {
            private readonly GRBackendRenderTarget _backendRenderTarget;
            private readonly OpenGlSurfaceRenderingSession _openGlSession;

            public OpenGlGpuSession(GRContext grContext,
                GRBackendRenderTarget backendRenderTarget,
                SKSurface surface,
                OpenGlSurfaceRenderingSession OpenGlSession)
            {
                GrContext = grContext;
                _backendRenderTarget = backendRenderTarget;
                SkSurface = surface;
                _openGlSession = OpenGlSession;

                SurfaceOrigin = OpenGlSession.IsYFlipped ? GRSurfaceOrigin.TopLeft : GRSurfaceOrigin.BottomLeft;
            }

            public void Dispose()
            {
                SkSurface.Canvas.Flush();

                SkSurface.Dispose();
                _backendRenderTarget.Dispose();
                GrContext.Flush();

                _openGlSession.Dispose();
            }

            public GRContext GrContext { get; }
            public SKSurface SkSurface { get; }
            public double ScaleFactor => _openGlSession.Scaling;
            public GRSurfaceOrigin SurfaceOrigin { get; }
        }
    }
}
