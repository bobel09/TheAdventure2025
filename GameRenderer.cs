using Silk.NET.Maths;
using Silk.NET.SDL;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using TheAdventure.Models;
using Point = Silk.NET.SDL.Point;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;
using SixLabors.Fonts;
using SixLabors.ImageSharp.Drawing;


namespace TheAdventure;

public unsafe class GameRenderer
{
    private Sdl _sdl;
    private Renderer* _renderer;
    private GameWindow _window;
    private Camera _camera;

    private Dictionary<int, IntPtr> _texturePointers = new();
    private Dictionary<int, TextureData> _textureData = new();
    private int _textureId;

    public GameRenderer(Sdl sdl, GameWindow window)
    {
        _sdl = sdl;

        _renderer = (Renderer*)window.CreateRenderer();
        _sdl.SetRenderDrawBlendMode(_renderer, BlendMode.Blend);

        _window = window;
        var windowSize = window.Size;
        _camera = new Camera(windowSize.Width, windowSize.Height);
    }

    public void SetWorldBounds(Rectangle<int> bounds)
    {
        _camera.SetWorldBounds(bounds);
    }

    public void CameraLookAt(int x, int y)
    {
        _camera.LookAt(x, y);
    }

    public int LoadTexture(string fileName, out TextureData textureInfo)
    {
        using (var fStream = new FileStream(fileName, FileMode.Open))
        {
            var image = Image.Load<Rgba32>(fStream);
            textureInfo = new TextureData()
            {
                Width = image.Width,
                Height = image.Height
            };
            var imageRAWData = new byte[textureInfo.Width * textureInfo.Height * 4];
            image.CopyPixelDataTo(imageRAWData.AsSpan());
            fixed (byte* data = imageRAWData)
            {
                var imageSurface = _sdl.CreateRGBSurfaceWithFormatFrom(data, textureInfo.Width,
                    textureInfo.Height, 8, textureInfo.Width * 4, (uint)PixelFormatEnum.Rgba32);
                if (imageSurface == null)
                {
                    throw new Exception("Failed to create surface from image data.");
                }

                var imageTexture = _sdl.CreateTextureFromSurface(_renderer, imageSurface);
                if (imageTexture == null)
                {
                    _sdl.FreeSurface(imageSurface);
                    throw new Exception("Failed to create texture from surface.");
                }

                _sdl.FreeSurface(imageSurface);

                _textureData[_textureId] = textureInfo;
                _texturePointers[_textureId] = (IntPtr)imageTexture;
            }
        }

        return _textureId++;
    }

    public void RenderTexture(int textureId, Rectangle<int> src, Rectangle<int> dst,
        RendererFlip flip = RendererFlip.None, double angle = 0.0, Point center = default)
    {
        if (_texturePointers.TryGetValue(textureId, out var imageTexture))
        {
            var translatedDst = _camera.ToScreenCoordinates(dst);
            _sdl.RenderCopyEx(_renderer, (Texture*)imageTexture, in src,
                in translatedDst,
                angle,
                in center, flip);
        }
    }

    public Vector2D<int> ToWorldCoordinates(int x, int y)
    {
        return _camera.ToWorldCoordinates(new Vector2D<int>(x, y));
    }

    public void SetDrawColor(byte r, byte g, byte b, byte a)
    {
        _sdl.SetRenderDrawColor(_renderer, r, g, b, a);
    }

    public void ClearScreen()
    {
        _sdl.RenderClear(_renderer);
    }

    public void PresentFrame()
    {
        _sdl.RenderPresent(_renderer);
    }

    public int CreateTextTexture(string text, string fontPath, int fontSize, Rgba32 color, out TextureData textureData)
    {
        var fontCollection = new SixLabors.Fonts.FontCollection();
        var font = fontCollection.Add(fontPath).CreateFont(fontSize);

        var textOptions = new SixLabors.ImageSharp.Drawing.Processing.RichTextOptions(font)
        {
            HorizontalAlignment = SixLabors.Fonts.HorizontalAlignment.Left
        };
        var bounds = TextMeasurer.MeasureBounds(text, textOptions);
        int width = (int)Math.Ceiling(bounds.Width);
        int height = (int)Math.Ceiling(bounds.Height);

        float offsetX = -bounds.Left;
        float offsetY = -bounds.Top;

        using var image = new Image<Rgba32>(width, height);
        image.Mutate(ctx =>
        {
            ctx.Clear(SixLabors.ImageSharp.Color.Transparent);
            ctx.DrawText(
                new SixLabors.ImageSharp.Drawing.Processing.RichTextOptions(font)
                {
                    Origin = new PointF(offsetX, offsetY),
                    HorizontalAlignment = SixLabors.Fonts.HorizontalAlignment.Left
                },
                text,
                color
            );
        });

        textureData = new TextureData { Width = width, Height = height };
        var imageRAWData = new byte[width * height * 4];
        image.CopyPixelDataTo(imageRAWData.AsSpan());

        fixed (byte* data = imageRAWData)
        {
            var imageSurface = _sdl.CreateRGBSurfaceWithFormatFrom(data, width, height, 8, width * 4, (uint)PixelFormatEnum.Rgba32);
            var imageTexture = _sdl.CreateTextureFromSurface(_renderer, imageSurface);
            _sdl.FreeSurface(imageSurface);

            _textureData[_textureId] = textureData;
            _texturePointers[_textureId] = (IntPtr)imageTexture;
        }

        return _textureId++;
    }

    public void DrawHealthBar(int currentHealth, int maxHealth, int screenX, int screenY, int width = 200, int height = 20)
    {
        float healthPercent = currentHealth / (float)maxHealth;
        int filledWidth = (int)(width * healthPercent);

        SetDrawColor(80, 0, 0, 255);
        var bgRect = new Rectangle<int>(screenX, screenY, width, height);
        _sdl.RenderFillRect(_renderer, &bgRect);

        SetDrawColor(255, 0, 0, 255);
        var fgRect = new Rectangle<int>(screenX, screenY, filledWidth, height);
        _sdl.RenderFillRect(_renderer, &fgRect);
    }
    public void DrawGameOverScreen(bool highlightRestart)
    {
        SetDrawColor(0, 0, 0, 180);
        ClearScreen();

        var fontPath = System.IO.Path.Combine("Assets", "Fonts", "Kanit-Black.ttf");
        int centerX = _camera.Width / 2;

        var gameOverTextId = CreateTextTexture(
            "Game Over",
            fontPath,
            48,
            new Rgba32(0, 0, 0, 255),
            out var gameOverData
        );

        int bgPadding = 16;
        var bgRect = new Rectangle<int>(
            centerX - gameOverData.Width / 2 - bgPadding,
            20 - bgPadding / 2,
            gameOverData.Width + bgPadding * 2,
            gameOverData.Height + bgPadding
        );
        SetDrawColor(255, 255, 255, 255);
        _sdl.RenderFillRect(_renderer, &bgRect);

        var textDst = new Rectangle<int>(
            centerX - gameOverData.Width / 2,
            20,
            gameOverData.Width,
            gameOverData.Height
        );
        RenderTexture(gameOverTextId, new Rectangle<int>(0, 0, gameOverData.Width, gameOverData.Height), textDst);
        var fontPathBtn = System.IO.Path.Combine("Assets", "Fonts", "Kanit-Black.ttf");

        var restartColor = highlightRestart ? new Rgba32(0, 255, 0, 255) : new Rgba32(100, 200, 100, 255);
        var quitColor = !highlightRestart ? new Rgba32(255, 0, 0, 255) : new Rgba32(200, 100, 100, 255);

        var restartTextId = CreateTextTexture(
            "Restart",
            fontPathBtn,
            36,
            restartColor,
            out var restartData
        );
        var quitTextId = CreateTextTexture(
            "Quit",
            fontPathBtn,
            36,
            quitColor,
            out var quitData
        );

        int yStart = 20 + gameOverData.Height + 20;
        var restartRect = new Rectangle<int>(
            centerX - restartData.Width / 2,
            yStart,
            restartData.Width,
            restartData.Height
        );
        var quitRect = new Rectangle<int>(
            centerX - quitData.Width / 2,
            yStart + restartData.Height + 10,
            quitData.Width,
            quitData.Height
        );

        RenderTexture(restartTextId, new Rectangle<int>(0, 0, restartData.Width, restartData.Height), restartRect);
        RenderTexture(quitTextId, new Rectangle<int>(0, 0, quitData.Width, quitData.Height), quitRect);
    }
}