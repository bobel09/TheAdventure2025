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
    private readonly List<int> _coinIds = new();

    private Level _currentLevel = new();
    private PlayerObject? _player;

    private DateTimeOffset _lastUpdate = DateTimeOffset.Now;
    private DateTimeOffset? _gameOverTime = null;

    private bool _gameOverHighlightRestart = true; 
    private bool _gameOverHandled = false;

    private bool _lastTabPressed = false;

    private readonly Random _random = new();

    private int _activeCoinCount = 0;
    private const int CoinsPerBatch = 10;

    private double _bombSpawnTimer = 0;
    private const double BombSpawnInterval = 2_000; 

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
            if (_gameOverTime == null)
            {
                _gameOverTime = currentTime;
                _gameOverHighlightRestart = true; 
                _gameOverHandled = false;
                _lastTabPressed = false; 
            }

            if ((currentTime - _gameOverTime.Value).TotalSeconds >= 1)
            {
                bool tabPressed = _input.isTabKeyPressed();

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

        // Only spawn a new batch if there are no active coins
        if (_activeCoinCount == 0)
        {
            SpawnCoinBatch();
        }

        _bombSpawnTimer += msSinceLastFrame;
        if (_bombSpawnTimer >= GetBombSpawnInterval())
        {
            _bombSpawnTimer = 0;

            // Increase bombs per batch as score increases (minimum 2, +1 every 10 points)
            int bombsPerBatch = 2 + _player.Score;
            SpawnBombBatch(bombsPerBatch);
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

        // Always use fixed screen coordinates for UI:
        _renderer.DrawHealthBar(_player.CurrentHealth, _player.MaxHealth, 10, 10, 200, 20);

        var fontPath = System.IO.Path.Combine("Assets", "Fonts", "Kanit-Black.ttf");
        _renderer.DrawScore(_player.Score, 10, 40, fontPath);

        _renderer.PresentFrame();
    }

    public void RenderAllObjects()
    {
        var toRemove = new List<int>();
        foreach (var gameObject in GetRenderables())
        {
            gameObject.Render(_renderer);

            if (_coinIds.Contains(gameObject.Id) && _player != null)
            {
                var coin = (CoinObject)gameObject;
                var deltaX = Math.Abs(_player.Position.X - coin.Position.X);
                var deltaY = Math.Abs(_player.Position.Y - coin.Position.Y);
                if (deltaX < 32 && deltaY < 32)
                {
                    _player.AddScore(1);
                    toRemove.Add(coin.Id);
                    _activeCoinCount--; 
                }
            }

            if (gameObject is TemporaryGameObject { IsExpired: true } tempGameObject)
            {
                toRemove.Add(tempGameObject.Id);
            }
        }

        foreach (var id in toRemove)
        {
            _gameObjects.Remove(id, out var gameObject);
            _coinIds.Remove(id); 

            if (_player == null)
                continue;

            if (gameObject is TemporaryGameObject tempGameObject)
            {
                var deltaX = Math.Abs(_player.Position.X - tempGameObject.Position.X);
                var deltaY = Math.Abs(_player.Position.Y - tempGameObject.Position.Y);
                if (deltaX < 32 && deltaY < 32)
                {
                    _player.TakeDamage(20);
                }
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

    public void AddCoin(int x, int y)
    {
        var spriteSheet = SpriteSheet.Load(_renderer, "coin.json", "Assets");
        var coin = new CoinObject(spriteSheet, (x, y));
        _gameObjects.Add(coin.Id, coin);
        _coinIds.Add(coin.Id);
    }

    private void SpawnCoinBatch()
    {
        int worldWidth = _currentLevel.Width.Value * _currentLevel.TileWidth.Value;
        int worldHeight = _currentLevel.Height.Value * _currentLevel.TileHeight.Value;

        for (int i = 0; i < CoinsPerBatch; i++)
        {
            int x = _random.Next(0, worldWidth);
            int y = _random.Next(0, worldHeight);
            AddCoin(x, y);
        }
        _activeCoinCount = CoinsPerBatch;
    }

    private void SpawnBombBatch(int count)
    {
        int worldWidth = _currentLevel.Width.Value * _currentLevel.TileWidth.Value;
        int worldHeight = _currentLevel.Height.Value * _currentLevel.TileHeight.Value;

        for (int i = 0; i < count; i++)
        {
            int x = _random.Next(0, worldWidth);
            int y = _random.Next(0, worldHeight);
            AddBomb(x, y, false);
        }
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

    private double GetBombSpawnInterval()
    {

        if (_player == null) return BombSpawnInterval;
        double interval = BombSpawnInterval - (_player.Score / 5) * 100; 
        return Math.Max(100, interval);
    }
}