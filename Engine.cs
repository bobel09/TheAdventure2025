using System.Reflection;
using System.Text.Json;
using Silk.NET.Maths;
using TheAdventure.Models;
using TheAdventure.Models.Data;
using TheAdventure.Scripting;

namespace TheAdventure;

public class Engine
{
    private readonly GameRenderer _renderer;
    private readonly Input _input;
    private readonly ScriptEngine _scriptEngine = new();

    private readonly Dictionary<int, GameObject> _gameObjects = new();
    private readonly Dictionary<string, TileSet> _loadedTileSets = new();
    private readonly Dictionary<int, Tile> _tileIdMap = new();

    private Level _currentLevel = new();
    private PlayerObject? _player;

    private DateTimeOffset _lastUpdate = DateTimeOffset.Now;
    private DateTimeOffset? _gameOverTime = null;

    private bool _gameOverHighlightRestart = true; 
    private bool _gameOverHandled = false;

    private bool _lastUpPressed = false;
    private bool _lastDownPressed = false;
    private bool _lastTabPressed = false;

    public Engine(GameRenderer renderer, Input input)
    {
        _renderer = renderer;
        _input = input;

        _input.OnMouseClick += (_, coords) => AddBomb(coords.x, coords.y);
    }

    public void SetupWorld()
    {
        _player = new(SpriteSheet.Load(_renderer, "Player.json", "Assets"), 100, 100);

        var levelContent = File.ReadAllText(Path.Combine("Assets", "terrain.tmj"));
        var level = JsonSerializer.Deserialize<Level>(levelContent);
        if (level == null)
        {
            throw new Exception("Failed to load level");
        }

        _tileIdMap.Clear();

        foreach (var tileSetRef in level.TileSets)
        {
            var tileSetContent = File.ReadAllText(Path.Combine("Assets", tileSetRef.Source));
            var tileSet = JsonSerializer.Deserialize<TileSet>(tileSetContent);
            if (tileSet == null)
            {
                throw new Exception("Failed to load tile set");
            }

            foreach (var tile in tileSet.Tiles)
            {
                tile.TextureId = _renderer.LoadTexture(Path.Combine("Assets", tile.Image), out _);
                _tileIdMap.Add(tileSetRef.FirstGID!.Value + tile.Id!.Value, tile);

                if (!_loadedTileSets.ContainsKey(tileSet.Name))
                {
                    _loadedTileSets.Add(tileSet.Name, tileSet);
                }
                else
                {
                    
                }
            }
        }

        if (level.Width == null || level.Height == null)
        {
            throw new Exception("Invalid level dimensions");
        }

        if (level.TileWidth == null || level.TileHeight == null)
        {
            throw new Exception("Invalid tile dimensions");
        }

        _renderer.SetWorldBounds(new Rectangle<int>(0, 0, level.Width.Value * level.TileWidth.Value,
            level.Height.Value * level.TileHeight.Value));

        _currentLevel = level;

        _scriptEngine.LoadAll(Path.Combine("Assets", "Scripts"));
        } 
        

    public void ProcessFrame()
    {
        var currentTime = DateTimeOffset.Now;
        var msSinceLastFrame = (currentTime - _lastUpdate).TotalMilliseconds;
        _lastUpdate = currentTime;

        if (_player == null)
        {
            return;
        }

        if (_player.State.State == PlayerObject.PlayerState.GameOver)
        {
            // Only allow input after 1 second
            if (_gameOverTime == null)
            {
                _gameOverTime = currentTime;
                _gameOverHighlightRestart = true; // Always default to Restart
                _gameOverHandled = false;
                _lastTabPressed = false; // Add this field to your class: private bool _lastTabPressed = false;
            }

            if ((currentTime - _gameOverTime.Value).TotalSeconds >= 1)
            {
                bool tabPressed = _input.isTabKeyPressed();

                // Edge detection for Tab key
                if (tabPressed && !_lastTabPressed)
                {
                    _gameOverHighlightRestart = !_gameOverHighlightRestart;
                }
                _lastTabPressed = tabPressed;

                if (IsSelectKeyPressed())
                {
                    if (!_gameOverHandled)
                    {
                        _gameOverHandled = true;
                        if (_gameOverHighlightRestart)
                        {
                            RestartGame();
                        }
                        else
                        {
                            QuitGame();
                        }
                    }
                }
            }
            return;
        }

        double up = _input.IsUpPressed() ? 1.0 : 0.0;
        double down = _input.IsDownPressed() ? 1.0 : 0.0;
        double left = _input.IsLeftPressed() ? 1.0 : 0.0;
        double right = _input.IsRightPressed() ? 1.0 : 0.0;
        bool isAttacking = _input.IsKeyAPressed() && (up + down + left + right <= 1);
        bool addBomb = _input.IsKeyBPressed();

        _player.UpdatePosition(up, down, left, right, 48, 48, msSinceLastFrame);
        if (isAttacking)
        {
            _player.Attack();
        }
        
        _scriptEngine.ExecuteAll(this);

        if (addBomb)
        {
            AddBomb(_player.Position.X, _player.Position.Y, false);
        }
    }

    public void RenderFrame()
    {
        _renderer.SetDrawColor(0, 0, 0, 255);
        _renderer.ClearScreen();

        if (_player != null && _player.State.State == PlayerObject.PlayerState.GameOver)
        {
           
                _renderer.DrawGameOverScreen(_gameOverHighlightRestart);
                _renderer.PresentFrame();
                return;
            
        }

        var playerPosition = _player!.Position;
        _renderer.CameraLookAt(playerPosition.X, playerPosition.Y);

        RenderTerrain();
        RenderAllObjects();

        _renderer.DrawHealthBar(_player.CurrentHealth, _player.MaxHealth, 10, 10, 200, 20);

        _renderer.PresentFrame();
    }


    public void RenderAllObjects()
    {
        var toRemove = new List<int>();
        foreach (var gameObject in GetRenderables())
        {
            gameObject.Render(_renderer);
            if (gameObject is TemporaryGameObject { IsExpired: true } tempGameObject)
            {
                toRemove.Add(tempGameObject.Id);
            }
        }

        foreach (var id in toRemove)
        {
            _gameObjects.Remove(id, out var gameObject);

            if (_player == null)
            {
                continue;
            }

            var tempGameObject = (TemporaryGameObject)gameObject!;
            var deltaX = Math.Abs(_player.Position.X - tempGameObject.Position.X);
            var deltaY = Math.Abs(_player.Position.Y - tempGameObject.Position.Y);
            if (deltaX < 32 && deltaY < 32)
            {
                _player.TakeDamage(20); 
            }
        }

        _player?.Render(_renderer);
    }

    public void RenderTerrain()
    {
        foreach (var currentLayer in _currentLevel.Layers)
        {
            if (currentLayer.Width == null || currentLayer.Data == null)
                continue;

            for (int i = 0; i < currentLayer.Width.Value; ++i)
            {
                for (int j = 0; j < currentLayer.Height; ++j)
                {
                    int dataIndex = j * currentLayer.Width.Value + i;
                    int? tileIdNullable = currentLayer.Data[dataIndex];

                    if (tileIdNullable == null)
                        continue;

                    int tileId = tileIdNullable.Value;

                    if (tileId == 0) 
                        continue;

                    if (!_tileIdMap.ContainsKey(tileId))
                        continue;

                    var currentTile = _tileIdMap[tileId];
                    var tileWidth = currentTile.ImageWidth ?? 0;
                    var tileHeight = currentTile.ImageHeight ?? 0;

                    var sourceRect = new Rectangle<int>(0, 0, tileWidth, tileHeight);
                    var destRect = new Rectangle<int>(i * tileWidth, j * tileHeight, tileWidth, tileHeight);
                    _renderer.RenderTexture(currentTile.TextureId, sourceRect, destRect);
                }
            }
        }
    }

    public IEnumerable<RenderableGameObject> GetRenderables()
    {
        foreach (var gameObject in _gameObjects.Values)
        {
            if (gameObject is RenderableGameObject renderableGameObject)
            {
                yield return renderableGameObject;
            }
        }
    }

    public (int X, int Y) GetPlayerPosition()
    {
        return _player!.Position;
    }

    public void AddBomb(int X, int Y, bool translateCoordinates = true)
    {
        var worldCoords = translateCoordinates ? _renderer.ToWorldCoordinates(X, Y) : new Vector2D<int>(X, Y);

        SpriteSheet spriteSheet = SpriteSheet.Load(_renderer, "BombExploding.json", "Assets");
        spriteSheet.ActivateAnimation("Explode");

        TemporaryGameObject bomb = new(spriteSheet, 2.1, (worldCoords.X, worldCoords.Y));
        _gameObjects.Add(bomb.Id, bomb);
    }

    private void ClearAndPresentScreen()
    {
        _renderer.SetDrawColor(0, 0, 0, 255);
        _renderer.ClearScreen();
        _renderer.PresentFrame();
    }

    private void RestartGame()
    {
        ClearAndPresentScreen();
        _gameOverHandled = false;
        _gameOverTime = null;
        _gameOverHighlightRestart = true;
        SetupWorld();
    }

    private void QuitGame()
    {
        ClearAndPresentScreen();
        Environment.Exit(0);
    }

    private bool IsSelectKeyPressed()
    {
   
        return _input.isSpaceKeyPressed() || _input.isEnterKeyPressed();
    }
}