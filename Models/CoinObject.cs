using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TheAdventure.Models;

public class CoinObject : RenderableGameObject
{
    public CoinObject(SpriteSheet spriteSheet, (int X, int Y) position)
        : base(spriteSheet, position)
    {
        spriteSheet.ActivateAnimation("Idle");
    }

    public override void Render(GameRenderer renderer)
    {
        SpriteSheet.Render(renderer, Position);
    }
}