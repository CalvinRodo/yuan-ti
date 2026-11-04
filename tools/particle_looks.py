"""Write the engine's spell particle looks: engine/game/looks/particles/*.json and particle_looks.json.

Every part of each of the original's 196 spell looks gets particles in both styles of
[display] spell_effects (Pixel: the region's palette; Modern: colours and glow). A look is an
archetype (how particles move: a gathering at the caster, a bolt, a rising column, a ward
ring...) in a theme (its colours). Change this file and run it rather than editing the JSON:

    uv run python3 tools/particle_looks.py

Pixel palettes use only indexes the original's spell OJFFs draw with, so they are the same in
every region (240-248, 1-5 and 11-15 turn with colour cycling, as in the originals).
"""
import json
import os
import sys
from dataclasses import dataclass, field

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'engine', 'game', 'looks')


@dataclass
class Theme:
    palette: list            # pixel style: palette indexes, light to dark, over a life
    colors: list             # modern style: colours over a life
    glow: str | None = None  # modern style: light thrown on the ground
    blend: str = 'Add'       # Mix for smoke and darkness (added colour can only lighten)
    random: bool = False     # each particle one of the colours (a rainbow)


T = {
    'arcane': Theme([46, 220, 161, 160], ['#ffffff', '#8fe8ff', '#3a5cff00'], '#4a78ff'),
    'holy': Theme([46, 173, 223, 167, 166], ['#ffffff', '#fff4b0', '#ffd050', '#c0801000'], '#ffd870'),
    'dark': Theme([8, 37, 48, 208], ['#c070ff', '#5020a0f0', '#180828c0', '#08000800'], None, 'Mix'),
    'necro': Theme([219, 4, 191, 208], ['#c8ff98e0', '#40a040d0', '#103010a0', '#04100400'], None, 'Mix'),
    'blood': Theme([79, 32, 222, 202], ['#ffa0a0', '#d02020e0', '#600000a0', '#20000000'], None, 'Mix'),
    'poison': Theme([57, 56, 55, 54, 53], ['#d8f0a8c0', '#78a858b0', '#38582880', '#18281000'], None, 'Mix'),
    'acid': Theme([219, 3, 4, 5], ['#f0ffa0', '#a0f040', '#40a01800'], '#80e040'),
    'fire': Theme([46, 167, 244, 245, 246, 247, 248], ['#fff6d0', '#ffc040', '#ff6010', '#80200000'], '#ff8030'),
    'cold': Theme([46, 217, 73, 215, 213], ['#ffffff', '#d8f6ff', '#90c8ff', '#6090ff00'], '#a0d8ff'),
    'bolt': Theme([46, 220, 161, 13, 12, 11], ['#ffffff', '#c0f8ff', '#5090ff00'], '#80c8ff'),
    'mind': Theme([46, 13, 12, 11], ['#ffffff', '#c8a8ff', '#7040ff00'], '#9060ff'),
    'charm': Theme([46, 79, 178, 177], ['#ffffff', '#ffb8e8', '#e040d0', '#80108000'], '#ff60d0'),
    'hold': Theme([46, 73, 72, 162], ['#ffffff', '#c0f0ff', '#60b0d0', '#2060a000'], '#80d0ff'),
    'chaos': Theme([223, 166, 164, 178, 7, 161, 220, 219],
                   ['#fff060', '#ffa020', '#ff4848', '#f050e8', '#a868ff', '#6890ff', '#58f0f0', '#68ff68'], None, 'Mix', random=True),
    'heal': Theme([46, 47, 43], ['#ffffff', '#f4ff90', '#90f05000'], '#e0ff90'),
    'ward': Theme([46, 8, 7, 10, 6], ['#ffffff', '#e8b8ff', '#a848ff', '#5010c000'], '#a060ff'),
    'stone': Theme([216, 213, 211, 209], ['#f0f0f0', '#a8a8a8', '#606060c0', '#30303000'], None, 'Mix'),
    'nature': Theme([43, 42, 55, 54, 205], ['#e8ffa0', '#88c848', '#408828c0', '#20401000'], None, 'Mix'),
    'sand': Theme([199, 172, 171, 170], ['#f0dcb0c0', '#c8a870a0', '#a0805000'], None, 'Mix'),
    'water': Theme([46, 73, 72, 40, 161], ['#ffffff', '#a8e0ff', '#4090e0', '#1040a000'], '#60b0ff'),
    'air': Theme([46, 52, 51, 217], ['#ffffffc0', '#d8e8ffa0', '#a0b8e000'], '#d0e0ff'),
    'shadow': Theme([48, 208, 254], ['#686888d0', '#202030a0', '#00000000'], None, 'Mix'),
    'illusion': Theme([46, 220, 73, 162], ['#ffffff', '#a8f0ffc0', '#60c0ff00'], '#80e0ff'),
    'speed': Theme([46, 223, 47, 167], ['#ffffff', '#fff080', '#ffc03000'], '#fff080'),
    'slow': Theme([73, 72, 161, 160], ['#c8e4ffe0', '#6090d0c0', '#2040a000'], None, 'Mix'),
    'fear': Theme([222, 202, 163, 208], ['#ff6848e0', '#a01010c0', '#30000080', '#10000000'], None, 'Mix'),
    'might': Theme([46, 167, 165, 164], ['#ffffff', '#ffd060', '#ff7020', '#a0200000'], '#ff9040'),
    'force': Theme([46, 162, 178], ['#ffffff', '#a8c8ff', '#e040e0', '#8020a000'], '#c080ff'),
    'light': Theme([46, 223, 8, 13, 220], ['#ffffff', '#fff080', '#e8a8ff', '#a8a8ff', '#88ffff'], '#ffffe0', random=True),
    'web': Theme([52, 51, 50], ['#f4f4ffe0', '#c8c8d8b0', '#80809000'], None, 'Mix'),
    'grease': Theme([210, 205, 194, 209], ['#706050f0', '#403028e0', '#20181000'], None, 'Mix'),
    'fog': Theme([217, 216, 215, 214], ['#ffffffa0', '#e8e8f090', '#c8c8d800'], None, 'Mix'),
    'insects': Theme([208, 209, 194, 205], ['#302820', '#201810', '#10080800'], None, 'Mix'),
    'psigreen': Theme([46, 219, 4, 5], ['#ffffff', '#90ffa0', '#20c04000'], '#50ff70'),
    'psimag': Theme([46, 79, 178, 177], ['#ffffff', '#ffb0c8', '#e040e0', '#80108000'], '#e060e0'),
    'death': Theme([46, 3, 4, 208], ['#ffffff', '#88ffb8', '#20a060', '#00301000'], '#40ff90'),
    'detonate': Theme([46, 223, 66, 212], ['#ffffff', '#fff080', '#ffb040', '#80808000'], '#fff0a0'),
}

DEFS = {}


def pixel(t, **d):
    """The pixel style: the theme's palette, never blended."""
    d['Palette'] = t.palette
    if t.random:
        d['RandomColor'] = True
    return d


def modern(t, glow=None, **d):
    d['Colors'] = t.colors
    d.setdefault('Texture', 'Glow')
    if t.blend == 'Add':
        d['Blend'] = 'Add'
    if t.random:
        d['RandomColor'] = True
    if t.glow and glow:
        d['Glow'] = dict(Radius=glow[0], Color=t.glow, Alpha=glow[1])
    return d


def look(name, t, px, md):
    DEFS[name + '.pixel'] = pixel(t, **px)
    DEFS[name + '.modern'] = modern(t, **md)
    return name


# Archetypes: how particles move. Each takes a theme and returns the name of the look (both styles).

def gather(t, n):
    """A cast: motes drawn in to the caster from a ring around it, then gone."""
    return look(n, t,
                dict(Size=[2, 1], Shape='Ring', Radius=14, Offset=[0, -8], Radial=True, Direction=180, Spread=10, Rate=80, Seconds=0.4,
                     Lifetime=[0.35, 0.5], Speed=[22, 32], Alpha=[0.6, 1, 1]),
                dict(Shape='Ring', Radius=16, Offset=[0, -8], Radial=True, Direction=170, Spread=10, Rate=160, Seconds=0.45,
                     Lifetime=[0.35, 0.5], Speed=[26, 38], Size=[1.5, 3, 3.5], Alpha=[0, 1, 0.6], glow=(22, 0.4)))


def flare(t, n):
    """A cast: sparks thrown up around the caster's feet."""
    return look(n, t,
                dict(Shape='Circle', Radius=8, Burst=14, Rate=30, Seconds=0.4, Lifetime=[0.3, 0.6], Speed=[6, 18], Direction=-90,
                     Spread=50, Gravity=[0, -12], Alpha=[1, 1, 0]),
                dict(Shape='Circle', Radius=8, Burst=30, Rate=70, Seconds=0.45, Lifetime=[0.3, 0.7], Speed=[8, 24], Direction=-90,
                     Spread=60, Gravity=[0, -18], Drag=1, Size=[3, 2, 0.5], Alpha=[1, 0.9, 0], glow=(26, 0.45)))


def ring(t, n, radius=12):
    """A ward: motes circling the one it is on, rising as they go."""
    return look(n, t,
                dict(Size=[2, 1], Shape='Ring', Radius=radius, Offset=[0, -2], Radial=True, Direction=90, Spread=5, Burst=10, Rate=40,
                     Seconds=0.6, Lifetime=[0.5, 0.8], Speed=[24, 30], Pull=50, Gravity=[0, -16], Alpha=[1, 1, 0]),
                dict(Shape='Ring', Radius=radius + 2, Offset=[0, -2], Radial=True, Direction=90, Spread=5, Burst=20, Rate=90,
                     Seconds=0.6, Lifetime=[0.5, 0.9], Speed=[28, 34], Pull=56, Gravity=[0, -20], Size=[2, 2.5, 1],
                     Alpha=[0, 1, 0], glow=(26, 0.4)))


def rise(t, n):
    """A blessing, a cure: a column rising off the one it is on."""
    return look(n, t,
                dict(Size=[2, 1], Shape='Circle', Radius=9, Burst=12, Rate=30, Seconds=0.6, Lifetime=[0.4, 0.8], Speed=[8, 20], Direction=-90,
                     Spread=12, Gravity=[0, -10], Alpha=[1, 1, 0]),
                dict(Shape='Circle', Radius=10, Burst=20, Rate=50, Seconds=0.6, Lifetime=[0.5, 0.9], Speed=[8, 22], Direction=-90,
                     Spread=12, Gravity=[0, -12], Size=[2.5, 2, 0.5], Alpha=[1, 0.9, 0], glow=(24, 0.45)))


def sink(t, n):
    """A curse: motes gathering over the one it is on and settling down onto it."""
    return look(n, t,
                dict(Size=[2, 1], Shape='Ring', Radius=12, Offset=[0, -34], Direction=90, Spread=20, Burst=8, Rate=30, Seconds=0.6,
                     Lifetime=[0.6, 0.9], Speed=[18, 30], Pull=20, Drag=0.5, Alpha=[0.5, 1, 1, 0]),
                dict(Shape='Ring', Radius=14, Offset=[0, -34], Direction=90, Spread=20, Burst=14, Rate=50, Seconds=0.6,
                     Lifetime=[0.6, 1.0], Speed=[18, 32], Pull=24, Drag=0.5, Size=[3, 6, 7], Alpha=[0, 0.9, 0.7, 0], glow=(20, 0.3)))


def swirl(t, n):
    """The mind: a swirl around the head of the one it is on."""
    return look(n, t,
                dict(Size=[2, 1], Shape='Ring', Radius=9, Offset=[0, -30], Radial=True, Direction=90, Spread=5, Burst=8, Rate=36, Seconds=0.6,
                     Lifetime=[0.4, 0.7], Speed=[30, 36], Pull=100, Alpha=[1, 1, 0]),
                dict(Shape='Ring', Radius=10, Offset=[0, -30], Radial=True, Direction=90, Spread=5, Burst=16, Rate=80, Seconds=0.6,
                     Lifetime=[0.4, 0.8], Speed=[32, 40], Pull=110, Size=[1.5, 2.5, 1], Alpha=[0, 1, 0], glow=(18, 0.35)))


def bind(t, n):
    """A hold: tight bands circling the one it is on."""
    return look(n, t,
                dict(Size=[2, 1], Shape='Ring', Radius=10, Offset=[0, -14], Radial=True, Direction=90, Spread=2, Burst=16, Rate=50, Seconds=0.6,
                     Lifetime=[0.3, 0.5], Speed=[40, 48], Pull=160, Alpha=[1, 1, 0]),
                dict(Shape='Ring', Radius=11, Offset=[0, -14], Radial=True, Direction=90, Spread=2, Burst=30, Rate=110, Seconds=0.6,
                     Lifetime=[0.3, 0.6], Speed=[44, 50], Pull=180, Size=[1.5, 2, 1], Texture='Spark', Alpha=[0, 1, 0], glow=(20, 0.4)))


def spark(t, n):
    """A hit: a spray out from the one reached."""
    return look(n, t,
                dict(Size=[2, 1], Shape='Circle', Radius=5, Offset=[0, -12], Burst=18, Seconds=0.1, Lifetime=[0.25, 0.5], Speed=[14, 30], Spread=180,
                     Drag=3, Alpha=[1, 0.6, 0]),
                dict(Shape='Circle', Radius=4, Offset=[0, -12], Burst=40, Rate=40, Seconds=0.2, Lifetime=[0.25, 0.55], Speed=[20, 50],
                     Spread=180, Drag=4, Size=[3, 1.5, 0.5], glow=(28, 0.55)))


def shimmer(t, n):
    """An illusion: glints winking in and out over the one it is on."""
    return look(n, t,
                dict(Shape='Circle', Radius=11, Offset=[0, -16], Rate=60, Seconds=0.7, Lifetime=[0.25, 0.6], Speed=[0, 5], Spread=180,
                     Alpha=[0, 1, 0]),
                dict(Shape='Circle', Radius=12, Offset=[0, -16], Rate=110, Seconds=0.7, Lifetime=[0.25, 0.6], Speed=[0, 6], Spread=180,
                     Size=[0.5, 3, 0.5], Alpha=[0, 1, 0], glow=(20, 0.3)))


def drain(t, n):
    """A draining: motes pulled off the one reached, back to the caster."""
    return look(n, t,
                dict(Size=[2, 1], Shape='Circle', Radius=6, Offset=[0, -14], Aim=True, Direction=180, Spread=14, Burst=10, Rate=40, Seconds=0.5,
                     Lifetime=[0.4, 0.7], Speed=[60, 110], Alpha=[1, 1, 0]),
                dict(Shape='Circle', Radius=6, Offset=[0, -14], Aim=True, Direction=180, Spread=14, Burst=20, Rate=90, Seconds=0.5,
                     Lifetime=[0.4, 0.8], Speed=[60, 120], Size=[2.5, 2, 0.5], Alpha=[1, 0.9, 0], glow=(20, 0.4)))


def puff(t, n):
    """A hit: a cloud puffing up around the one reached."""
    return look(n, t,
                dict(Shape='Circle', Radius=7, Burst=25, Rate=50, Seconds=0.4, Lifetime=[0.4, 0.7], Speed=[2, 6], Direction=-90, Spread=90,
                     Size=[1, 2], Alpha=[1, 1, 0]),
                dict(Shape='Circle', Radius=7, Burst=35, Rate=60, Seconds=0.4, Lifetime=[0.5, 0.8], Speed=[2, 8], Direction=-90, Spread=90,
                     Size=[3, 6], Alpha=[0.9, 0.7, 0]))


def blast(t, n):
    """A hit: an explosion on the one reached (a stone, a meteor)."""
    return look(n, t,
                dict(Shape='Circle', Radius=6, Offset=[0, -8], Burst=40, Seconds=0.1, Lifetime=[0.3, 0.6], Speed=[16, 44], Spread=180,
                     Gravity=[0, 30], Drag=2, Size=[2, 1], Alpha=[1, 1, 0]),
                dict(Shape='Circle', Radius=6, Offset=[0, -8], Burst=80, Rate=60, Seconds=0.15, Lifetime=[0.3, 0.7], Speed=[20, 60],
                     Spread=180, Gravity=[0, 40], Drag=2.5, Size=[4, 2.5, 1], Alpha=[1, 0.9, 0], glow=(40, 0.6)))


def buzz(t, n):
    """Insects: specks darting about the one reached."""
    return look(n, t,
                dict(Shape='Circle', Radius=12, Offset=[0, -14], Burst=14, Rate=30, Seconds=0.7, Lifetime=[0.4, 0.8], Speed=[30, 60],
                     Spread=180, Pull=180, Drag=2, Alpha=[1, 1, 0]),
                dict(Shape='Circle', Radius=12, Offset=[0, -14], Burst=24, Rate=50, Seconds=0.7, Lifetime=[0.4, 0.8], Speed=[30, 60],
                     Spread=180, Pull=180, Drag=2, Size=[1.5], Texture='Pixel', Alpha=[1, 1, 0]))


def summon(t, n):
    """A summoning: a vortex rising where the creature comes."""
    return look(n, t,
                dict(Size=[2, 1], Shape='Ring', Radius=16, Radial=True, Direction=100, Spread=5, Burst=16, Rate=70, Seconds=0.8,
                     Lifetime=[0.5, 0.8], Speed=[30, 36], Pull=60, Gravity=[0, -30], Alpha=[1, 1, 0]),
                dict(Shape='Ring', Radius=18, Radial=True, Direction=100, Spread=5, Burst=30, Rate=130, Seconds=0.8,
                     Lifetime=[0.5, 0.9], Speed=[32, 40], Pull=64, Gravity=[0, -34], Size=[2.5, 2, 0.5], Alpha=[0, 1, 0],
                     glow=(32, 0.5)))


def bolt(t, n, wander=0, heads=1):
    """A flight: a glowing bolt and its trail."""
    return look(n, t,
                dict(Heads=heads, Wander=wander, Rate=70, Lifetime=[0.15, 0.35], Speed=[0, 6], Spread=180, HeadSize=3, ArriveBurst=6,
                     Alpha=[1, 1, 0], Max=200),
                dict(Heads=heads, Wander=wander * 2, Rate=220, Lifetime=[0.15, 0.4], Speed=[0, 10], Spread=180, Drag=1, HeadSize=7,
                     ArriveBurst=16, Size=[3.5, 2, 0.5], Alpha=[1, 0.9, 0], Max=600, glow=(30, 0.5)))


def ray(t, n):
    """A flight: a thin, bright ray."""
    return look(n, t,
                dict(Rate=240, Lifetime=[0.1, 0.25], Speed=[0, 3], Spread=180, HeadSize=2, Alpha=[1, 1, 0], Max=300),
                dict(Rate=480, Lifetime=[0.1, 0.3], Speed=[0, 5], Spread=180, HeadSize=5, Size=[2, 1], Texture='Spark', Max=800,
                     glow=(22, 0.5)))


def rock(t, n):
    """A flight: a stone, shedding grit."""
    return look(n, t,
                dict(Rate=30, Lifetime=[0.2, 0.4], Speed=[4, 12], Direction=90, Spread=60, Gravity=[0, 40], HeadSize=4, ArriveBurst=10,
                     Max=120),
                dict(Rate=60, Lifetime=[0.2, 0.4], Speed=[4, 14], Direction=90, Spread=60, Gravity=[0, 50], HeadSize=6, ArriveBurst=20,
                     Size=[2, 1], Texture='Pixel', Max=200))


def motes(t, n, down=False):
    """A burst that helps or harms: motes rising (sinking) over each of its cells."""
    way = 90 if down else -90
    return look(n, t,
                dict(Shape='Cell', Offset=[0, -28 if down else 0], Burst=160, Rate=420, Seconds=0.35, Lifetime=[0.4, 0.8],
                     Speed=[6, 18], Direction=way, Spread=25, Gravity=[0, -way / 6], Size=[2, 1], Alpha=[1, 1, 0], Max=900),
                dict(Shape='Cell', Offset=[0, -28 if down else 0], Burst=220, Rate=600, Seconds=0.35, Lifetime=[0.4, 0.9],
                     Speed=[6, 20], Direction=way, Spread=25, Gravity=[0, -way / 5], Size=[2, 4, 1.5], Alpha=[0, 1, 0], Max=1400,
                     glow=(50, 0.4)))


def wave(t, n):
    """A burst that undoes: a wave running out over its cells."""
    return look(n, t,
                dict(Shape='Cell', Radial=True, Burst=60, Rate=120, Seconds=0.3, Lifetime=[0.3, 0.6], Speed=[30, 50], Spread=10, Drag=1.5,
                     Alpha=[1, 1, 0], Max=500),
                dict(Shape='Cell', Radial=True, Burst=110, Rate=220, Seconds=0.3, Lifetime=[0.3, 0.6], Speed=[30, 56], Spread=10, Drag=1.5,
                     Size=[2.5, 1.5, 0.5], Texture='Spark', Alpha=[1, 0.9, 0], Max=900, glow=(50, 0.45)))


def explode(t, n):
    """A burst: an explosion over its cells."""
    return look(n, t,
                dict(Shape='Cell', Burst=240, Rate=400, Seconds=0.3, Lifetime=[0.25, 0.6], Speed=[8, 34], Spread=180, Gravity=[0, -30],
                     Drag=2, Size=[2, 1], Alpha=[1, 1, 0.5, 0], Max=1200),
                dict(Shape='Cell', Burst=400, Rate=520, Seconds=0.3, Lifetime=[0.3, 0.7], Speed=[10, 46], Spread=180, Gravity=[0, -40],
                     Drag=2.5, Size=[5, 3, 1], Alpha=[1, 0.8, 0.4, 0], Max=1600, glow=(60, 0.55)))


def fall(t, n):
    """A burst from above: hail, a flame strike, falling onto its cells."""
    return look(n, t,
                dict(Shape='Cell', Offset=[0, -48], Burst=60, Rate=320, Seconds=0.35, Lifetime=[0.3, 0.34], Speed=[140, 160], Direction=90,
                     Spread=4, Size=[2], Alpha=[1], Max=700),
                dict(Shape='Cell', Offset=[0, -48], Burst=60, Rate=320, Seconds=0.35, Lifetime=[0.3, 0.34], Speed=[140, 160], Direction=90,
                     Spread=4, Size=[1, 1.5], Alpha=[0.7, 0.9], Texture='Spark', Max=900, glow=(56, 0.4)))


def cone(t, n):
    """A burst: a cone sprayed from the caster."""
    return look(n, t,
                dict(Shape='Cone', Direction=0, Spread=22, Burst=150, Rate=650, Seconds=0.35, Lifetime=[0.3, 0.6], Speed=[50, 120], Drag=1.5, Size=[2, 3],
                     Alpha=[1, 1, 0.4, 0], Max=1200),
                dict(Shape='Cone', Direction=0, Spread=24, Burst=220, Rate=900, Seconds=0.35, Lifetime=[0.3, 0.7], Speed=[60, 140], Drag=1.8,
                     Size=[1.5, 4, 7], Alpha=[1, 0.8, 0.4, 0], Max=1800, glow=(34, 0.4)))


def cloud(t, n):
    """A field: a cloud drifting over its cells."""
    return look(n, t,
                dict(Shape='Cell', Rate=12, Lifetime=[1.2, 2.0], Speed=[2, 6], Spread=180, Drag=0.5, Size=[2, 3, 3], Alpha=[0, 0.7, 0.7, 0],
                     Max=1000),
                dict(Shape='Cell', Rate=7, Lifetime=[1.4, 2.4], Speed=[2, 6], Spread=180, Drag=0.4, Size=[6, 12, 15],
                     Alpha=[0, 0.8, 0.7, 0], Max=800))


def flames(t, n):
    """A field: flames licking up off its cells."""
    return look(n, t,
                dict(Shape='Cell', Rate=20, Lifetime=[0.4, 0.8], Speed=[10, 24], Direction=-90, Spread=15, Alpha=[1, 1, 0.5, 0], Max=900),
                dict(Shape='Cell', Rate=20, Lifetime=[0.4, 0.8], Speed=[12, 30], Direction=-90, Spread=15, Gravity=[0, -10],
                     Size=[5, 3.5, 1], Alpha=[1, 0.9, 0.5, 0], Max=1100, glow=(40, 0.45)))


def shards(t, n):
    """A field: a wall's face glinting (ice, stone, force)."""
    return look(n, t,
                dict(Shape='Cell', Rate=6, Lifetime=[0.5, 1.0], Speed=[2, 8], Direction=-90, Spread=10, Size=[1, 2], Alpha=[0, 1, 0], Max=300),
                dict(Shape='Cell', Rate=6, Lifetime=[0.6, 1.1], Speed=[2, 8], Direction=-90, Spread=10, Size=[1, 3, 1], Texture='Spark',
                     Alpha=[0, 1, 0], Max=400, glow=(30, 0.25)))


def ground(t, n):
    """A field: something low over its cells (grease, vines, sand)."""
    return look(n, t,
                dict(Shape='Cell', Rate=4, Lifetime=[0.8, 1.4], Speed=[1, 4], Spread=180, Size=[1, 2, 1], Alpha=[0, 1, 0], Max=300),
                dict(Shape='Cell', Rate=4, Lifetime=[0.9, 1.6], Speed=[1, 4], Spread=180, Size=[2, 4, 2], Alpha=[0, 0.9, 0], Max=300))


def strands(t, n):
    """A field: a web's strands catching the light."""
    return look(n, t,
                dict(Shape='Cell', Rate=4, Lifetime=[0.8, 1.4], Speed=[3, 6], Spread=180, Size=[3], Texture='Spark', Alpha=[0, 1, 0], Max=300),
                dict(Shape='Cell', Rate=4, Lifetime=[0.9, 1.6], Speed=[3, 6], Spread=180, Size=[4], Texture='Spark', Alpha=[0, 0.9, 0], Max=300))


def glitter(t, n):
    """A field: glints winking over its cells."""
    return look(n, t,
                dict(Shape='Cell', Rate=6, Lifetime=[0.3, 0.7], Speed=[0, 3], Spread=180, Alpha=[0, 1, 0], Max=300),
                dict(Shape='Cell', Rate=6, Lifetime=[0.3, 0.7], Speed=[0, 3], Spread=180, Size=[0.5, 3, 0.5], Alpha=[0, 1, 0], Max=400,
                     glow=(30, 0.2)))


def swarm(t, n):
    """A field: insects darting over its cells."""
    return look(n, t,
                dict(Shape='Cell', Rate=6, Lifetime=[0.4, 0.8], Speed=[30, 60], Spread=180, Drag=3, Alpha=[1, 1, 0], Max=400),
                dict(Shape='Cell', Rate=8, Lifetime=[0.4, 0.8], Speed=[30, 60], Spread=180, Drag=3, Size=[1.5], Texture='Pixel',
                     Alpha=[1, 1, 0], Max=500))


ARCHETYPES = {f.__name__: f for f in [gather, flare, ring, rise, sink, swirl, bind, spark, shimmer, drain, puff, blast, buzz, summon,
                                      bolt, ray, rock, motes, wave, explode, fall, cone, cloud, flames, shards, ground, strands,
                                      glitter, swarm]}
ARCHETYPES['sinking'] = lambda t, n: motes(t, n, down=True)
ARCHETYPES['weave'] = lambda t, n: bolt(t, n, wander=18)
ARCHETYPES['meteors'] = lambda t, n: bolt(t, n, wander=8, heads=3)
ARCHETYPES['globe'] = lambda t, n: ring(t, n, radius=18)


def kept():
    """The first looks, made by hand: the starter set's particular ones."""
    def d(name, theme, px, md):
        look(name, T[theme], px, md)
    d('magic-missile-flight', 'arcane',
      # The original's one bolt, here three that weave a little: the same flight time.
      dict(Heads=3, Wander=10, Stagger=0.2, Rate=50, Lifetime=[0.12, 0.25], Speed=[0, 6], Spread=180, HeadSize=2, ArriveBurst=5,
           Alpha=[1, 0.7, 0], Max=200),
      # A swarm: five bolts loop out across the screen, then home in one after another.
      dict(Heads=5, Wander=120, Stagger=0.35, MinSeconds=1.0, Rate=150, Lifetime=[0.18, 0.4], Speed=[0, 8], Spread=180, Drag=2,
           HeadSize=6, ArriveBurst=16, Size=[2.5, 1.2, 0.3], Alpha=[1, 0.8, 0], Max=1000, glow=(18, 0.55)))
    DEFS['magic-missile-flight.modern']['Colors'] = ['#d8ecff', '#4a7cff', '#2228ff00']
    d('fire-flight', 'fire',
      dict(Rate=70, Lifetime=[0.2, 0.45], Speed=[2, 10], Direction=-90, Spread=180, Gravity=[0, -20], HeadSize=3, Alpha=[1, 1, 0], Max=200),
      dict(Rate=240, Lifetime=[0.2, 0.5], Speed=[4, 16], Direction=-90, Spread=180, Gravity=[0, -30], Drag=1, HeadSize=8,
           Size=[4.5, 2.5, 1], Alpha=[1, 0.9, 0], Max=600, glow=(40, 0.55)))
    DEFS['fire-flight.modern']['Colors'] = ['#fff6d0', '#ffc040', '#ff6010', '#80200000']
    # The fireball fills its circle, as the original's sheet of flame does; the modern one's colours
    # are darker (added onto sand they would turn white).
    d('fireball-burst', 'fire',
      dict(Shape='Cell', Burst=420, Rate=680, Seconds=0.35, Lifetime=[0.3, 0.6], Speed=[4, 22], Spread=180, Gravity=[0, -30], Drag=2,
           Size=[3, 2], Alpha=[1, 1, 0.5, 0], Max=1600),
      dict(Shape='Cell', Burst=400, Rate=520, Seconds=0.35, Lifetime=[0.3, 0.7], Speed=[6, 34], Spread=180, Gravity=[0, -40], Drag=2.5,
           Size=[6, 4, 1.5], Alpha=[1, 0.8, 0.4, 0], Max=1600, glow=(70, 0.5)))
    DEFS['fireball-burst.pixel']['Palette'] = [46, 244, 243, 242, 241, 165, 164, 163]
    DEFS['fireball-burst.modern']['Colors'] = ['#fff0b0', '#ffa030f0', '#e04810d0', '#80200080', '#30101000']
    DEFS['fireball-burst.modern'].pop('Blend')
    d('bolt-flight', 'bolt',
      dict(Heads=2, Wander=6, Rate=120, Lifetime=[0.08, 0.16], Speed=[0, 4], Spread=180, HeadSize=2, Max=200),
      dict(Heads=3, Wander=12, Rate=260, Lifetime=[0.06, 0.16], Speed=[0, 10], Spread=180, HeadSize=4, Size=[2, 1], Texture='Spark',
           Max=600, glow=(24, 0.5)))
    DEFS['bolt-flight.pixel']['Palette'] = [46, 220]
    d('lightning-burst', 'bolt',
      dict(Shape='Line', Burst=200, Rate=600, Seconds=0.3, Lifetime=[0.08, 0.25], Speed=[0, 14], Spread=180, Alpha=[1, 1, 0], Max=800),
      dict(Shape='Line', Burst=320, Rate=900, Seconds=0.3, Lifetime=[0.08, 0.25], Speed=[0, 30], Spread=180, Drag=4, Size=[2.5, 1],
           Texture='Spark', Max=1400, glow=(40, 0.6)))
    d('cone-of-cold-burst', 'cold',
      dict(Shape='Cone', Direction=0, Spread=22, Burst=150, Rate=650, Seconds=0.35, Lifetime=[0.3, 0.6], Speed=[50, 120], Drag=1.5, Size=[2, 3],
           Alpha=[1, 1, 0.4, 0], Max=1200),
      dict(Shape='Cone', Direction=0, Spread=24, Burst=220, Rate=900, Seconds=0.35, Lifetime=[0.3, 0.7], Speed=[60, 140], Drag=1.8, Size=[1.5, 4, 7],
           Alpha=[1, 0.8, 0.4, 0], Max=1800, glow=(34, 0.4)))
    DEFS['cone-of-cold-burst.modern']['Colors'] = ['#ffffff', '#d8f6ff', '#90c8ff80', '#6090ff00']
    d('cold-hit', 'cold',
      dict(Shape='Circle', Radius=8, Burst=20, Rate=20, Seconds=0.3, Lifetime=[0.3, 0.6], Speed=[2, 8], Direction=90, Spread=40,
           Gravity=[0, 10], Alpha=[1, 1, 0]),
      dict(Shape='Circle', Radius=8, Burst=30, Rate=40, Seconds=0.3, Lifetime=[0.3, 0.7], Speed=[2, 10], Direction=90, Spread=40,
           Gravity=[0, 12], Size=[2.5, 1.5], glow=(22, 0.4)))
    d('fire-hit', 'fire',
      dict(Shape='Circle', Radius=6, Burst=14, Rate=30, Seconds=0.3, Lifetime=[0.25, 0.5], Speed=[6, 18], Direction=-90, Spread=40,
           Gravity=[0, -20], Alpha=[1, 1, 0]),
      dict(Shape='Circle', Radius=6, Burst=30, Rate=60, Seconds=0.3, Lifetime=[0.25, 0.55], Speed=[8, 24], Direction=-90, Spread=50,
           Gravity=[0, -30], Size=[4, 2, 0.5], Alpha=[1, 0.8, 0], glow=(26, 0.5)))
    DEFS['fire-hit.modern']['Colors'] = ['#fff0c0', '#ffa030', '#ff401000']
    d('wall-of-fire-field', 'fire',
      dict(Shape='Cell', Rate=20, Lifetime=[0.4, 0.8], Speed=[10, 24], Direction=-90, Spread=15, Alpha=[1, 1, 0.5, 0], Max=900),
      dict(Shape='Cell', Rate=20, Lifetime=[0.4, 0.8], Speed=[12, 30], Direction=-90, Spread=15, Gravity=[0, -10], Size=[5, 3.5, 1],
           Alpha=[1, 0.9, 0.5, 0], Max=1100, glow=(40, 0.45)))
    DEFS['wall-of-fire-field.pixel']['Palette'] = [46, 244, 243, 242, 241, 240, 248]
    DEFS['wall-of-fire-field.modern']['Colors'] = ['#fff0b0', '#ffb030', '#ff5010', '#60100000']
    d('stinking-cloud-field', 'poison',
      dict(Shape='Cell', Rate=12, Lifetime=[1.2, 2.0], Speed=[2, 6], Spread=180, Drag=0.5, Size=[2, 3, 3], Alpha=[0, 0.7, 0.7, 0], Max=1000),
      dict(Shape='Cell', Rate=7, Lifetime=[1.4, 2.4], Speed=[2, 6], Spread=180, Drag=0.4, Size=[6, 12, 15], Alpha=[0, 0.8, 0.7, 0], Max=800))
    DEFS['stinking-cloud-field.pixel']['Palette'] = [173, 172, 171, 57, 56]
    DEFS['stinking-cloud-field.modern']['Colors'] = ['#d8e0a000', '#b0c88080', '#78a06070', '#40603000']
    d('dust-devil-arrival', 'sand',
      dict(Shape='Circle', Radius=12, Burst=30, Rate=80, Seconds=0.8, Lifetime=[0.5, 0.9], Speed=[14, 30], Direction=-90, Spread=40,
           Gravity=[0, -10], Drag=0.8, Size=[1, 2], Alpha=[1, 1, 0.5, 0]),
      dict(Shape='Circle', Radius=12, Burst=40, Rate=90, Seconds=0.8, Lifetime=[0.6, 1.1], Speed=[14, 34], Direction=-90, Spread=40,
           Gravity=[0, -12], Drag=0.8, Size=[3, 7, 10], Alpha=[0.9, 0.6, 0]))


# What each of the original's looks is drawn with: number, name (the monsters' own have none), and each part
# the original draws as part=archetype:theme (or part=name, a look made above). C cast, F flight, B burst,
# H hit, D field. Their parts are those DSUN.EXE draws; --check-spell-effects says if one is left out.
SPELLS = """
1 Armor | C=ring:ward
2 Burning Hands | B=cone:fire H=fire-hit
3 Charm Person | C=gather:charm H=swirl:charm
4 Chill Touch | H=sink:cold
5 Color Spray | B=cone:chaos H=spark:chaos
6 Enlarge | H=rise:might
7 Gaze Reflection | H=ring:illusion
8 Grease | C=gather:arcane F=bolt:grease H=puff:grease D=ground:grease
9 Magic Missile | C=flare:arcane F=magic-missile-flight H=spark:arcane
10 Shield | H=ring:ward
11 Shocking Grasp | H=spark:bolt
12 Wall Of Fog | C=gather:arcane H=puff:fog D=cloud:fog
13 Blur | H=shimmer:illusion
14 Detect Invisibility | C=ring:light
15 Flaming Sphere | B=explode:fire H=fire-hit
16 Fog Cloud | C=gather:arcane H=puff:fog D=cloud:fog
17 Glitterdust | C=gather:light H=shimmer:light D=glitter:light
18 Invisibility | H=shimmer:illusion
19 Melf's Acid Arrow | C=gather:acid F=bolt:acid H=spark:acid
20 Mirror Image | H=shimmer:illusion
21 Protection From Paralysis | H=ring:ward
22 Scare | C=gather:fear B=sinking:fear H=sink:fear
23 Stinking Cloud | C=gather:poison H=puff:poison D=stinking-cloud-field
24 Strength | H=rise:might
25 Web | C=gather:arcane H=puff:web D=strands:web
26 Blink | C=shimmer:illusion
27 Dispel Magic | C=gather:arcane B=wave:arcane H=spark:arcane
28 Fireball | F=fire-flight B=fireball-burst H=fire-hit
29 Flame Arrow | C=gather:fire F=bolt:fire H=fire-hit
30 Haste | C=gather:speed B=motes:speed H=rise:speed
31 Hold Person | C=gather:hold H=bind:hold
32 Hold Undead | C=gather:hold H=bind:hold
33 Lightning Bolt | C=flare:arcane F=bolt-flight B=lightning-burst H=spark:bolt
34 Melf's Minute Meteors | C=gather:fire F=meteors:fire H=blast:fire
35 Minor Malison | C=gather:dark B=sinking:dark H=sink:dark
36 Monster Summoning I | C=gather:arcane H=summon:arcane
37 Protection From Normal Missiles | C=gather:ward H=ring:ward
38 Slow | C=gather:slow B=sinking:slow H=sink:slow
39 Spirit Armor | C=ring:holy
40 Vampiric Touch | H=drain:necro
41 Charm Monster | C=gather:charm H=swirl:charm
42 Confusion | C=gather:chaos B=motes:chaos H=swirl:chaos
43 Evard's Black Tentacles | C=gather:dark H=rise:dark
44 Fear | C=gather:fear B=cone:fear H=sink:fear
45 Fire Shield | C=ring:fire
46 Ice Storm | C=gather:cold B=fall:cold H=cold-hit
47 Improved Invisibility | H=shimmer:illusion
48 Minor Globe Of Invulnerability | C=globe:ward
49 Minor Spell Turning | C=ring:illusion
50 Monster Summoning II | C=gather:arcane H=summon:arcane
51 Rainbow Pattern | C=gather:chaos H=shimmer:chaos D=glitter:chaos
52 Solid Fog | C=gather:arcane H=puff:fog D=cloud:fog
53 Stoneskin | H=ring:stone
54 Turn Pebble To Boulder | F=rock:stone H=blast:stone
55 Wall Of Fire | C=flare:fire H=fire-hit D=wall-of-fire-field
56 Wall Of Ice | C=gather:cold H=spark:cold D=shards:cold
57 Chaos | C=gather:chaos B=motes:chaos H=swirl:chaos
58 Cloudkill | C=gather:necro H=puff:necro D=cloud:necro
59 Cone Of Cold | B=cone-of-cold-burst H=cold-hit
60 Conjure Elemental | C=gather:arcane H=summon:arcane
61 Dismissal | C=gather:holy H=spark:holy
62 Domination | C=gather:mind H=swirl:mind
63 Feeblemind | C=gather:mind H=swirl:mind
64 Hold Monster | C=gather:hold H=bind:hold
65 Lower Resistance | C=gather:dark H=sink:dark
66 Monster Summoning III | C=gather:arcane H=summon:arcane
67 Summon Shadow | C=gather:shadow H=summon:shadow
68 Wall Of Force | C=gather:force H=spark:force D=shards:force
69 Wall Of Stone | C=gather:stone H=blast:stone D=shards:stone
70 Bless | C=gather:holy B=motes:holy H=rise:holy
71 Curse | C=gather:dark B=sinking:dark H=sink:dark
72 Cure Light Wounds | H=rise:heal
73 Cause Light Wounds | H=spark:blood
74 Entangle | C=gather:nature H=bind:nature D=ground:nature
75 Invisibility To Undead | H=shimmer:illusion
76 Magical Stone | F=rock:stone H=spark:stone
77 Protection From Evil | H=ring:holy
78 Remove Fear | C=gather:holy H=rise:holy
79 Cause Fear | C=gather:fear H=sink:fear
80 Shillelagh | C=ring:nature
81 Aid | H=rise:holy
82 Barkskin | H=ring:nature
83 Charm Person Or Mammal | C=gather:charm H=swirl:charm
84 Dust Devil | C=gather:sand H=dust-devil-arrival
85 Find Traps | C=ring:light
86 Flame Blade | C=ring:fire
87 Hold Person | C=gather:hold H=bind:hold
88 Resist Fire | H=ring:fire
89 Resist Cold | H=ring:cold
90 Spiritual Hammer | H=blast:arcane
91 Conjure Lesser Air Elemental | C=gather:air H=summon:air
92 Conjure Lesser Fire Elemental | C=gather:fire H=summon:fire
93 Conjure Lesser Earth Elemental | C=gather:stone H=summon:stone
94 Conjure Lesser Water Elemental | C=gather:water H=summon:water
95 Cure Blindness Or Deafness | H=rise:heal
96 Cause Blindness Or Deafness | H=sink:dark
97 Cure Disease | H=rise:heal
98 Cause Disease | H=sink:poison
99 Dispel Magic | C=gather:arcane B=wave:arcane H=spark:arcane
100 Magical Vestment | C=ring:holy
101 Negative Plane Protection | H=ring:holy
102 Prayer | C=gather:holy B=motes:holy H=rise:holy
103 Protection From Fire | H=ring:fire
104 Remove Curse | H=rise:holy
105 Bestow Curse | H=sink:dark
106 Remove Paralysis | C=gather:heal H=rise:heal
107 Summon Insects | C=gather:nature H=buzz:insects
108 Abjure | C=gather:holy H=spark:holy
109 Blood Flow | H=spark:blood
110 Cloak Of Bravery | H=ring:holy
111 Cloak Of Fear | H=ring:fear
112 Condense | F=bolt:water H=spark:water
113 Cure Serious Wounds | H=rise:heal
114 Cause Serious Wounds | H=spark:blood
115 Dehydrate | H=drain:water
116 Dust Cloud | C=gather:sand H=puff:sand D=cloud:sand
117 Focus Heat | C=gather:fire H=fire-hit
118 Free Action | H=rise:speed
119 Neutralize Poison | H=rise:heal
120 Poison | H=sink:poison
121 Produce Fire | C=flare:fire H=fire-hit D=wall-of-fire-field
122 Protection From Evil, 10' Radius | H=ring:holy
123 Protection From Lightning | H=ring:bolt
124 Conjure Air Elemental | C=gather:air H=summon:air
125 Conjure Fire Elemental | C=gather:fire H=summon:fire
126 Conjure Earth Elemental | C=gather:stone H=summon:stone
127 Conjure Water Elemental | C=gather:water H=summon:water
128 Cure Critical Wounds | H=rise:heal
129 Cause Critical Wounds | H=spark:blood
130 Deflection | C=ring:force
131 Dispel Evil | C=gather:holy H=spark:holy
132 Flame Strike | C=gather:fire B=fall:fire H=fire-hit
133 Insect Plague | C=gather:nature H=buzz:insects D=swarm:insects
134 Ironskin | H=ring:stone
135 Quicksand | C=gather:sand H=sink:sand D=ground:sand
136 Raise Dead | C=gather:holy H=rise:holy
137 Slay Living | C=gather:death F=ray:death H=sink:death
138 Wall Of Fire | C=flare:fire H=fire-hit D=wall-of-fire-field
139 Detonate | C=gather:detonate F=bolt:detonate H=blast:detonate
140 Disintegrate | C=gather:death F=ray:death H=spark:sand
141 Project Force | C=gather:force F=bolt:force H=spark:force
142 Ballistic Attack | C=gather:stone F=rock:stone H=spark:stone
143 Control Body | C=gather:mind H=bind:mind
144 Inertial Barrier | C=globe:force
145 Animal Affinity | C=ring:nature
146 Energy Containment | C=ring:bolt
147 Life Draining | H=drain:necro
148 Absorb Disease | H=drain:poison
149 Adrenalin Control | C=rise:might
150 Biofeedback | C=ring:heal
151 Body Weaponry | C=ring:stone
152 Cell Adjustment | C=rise:heal
153 Displacement | C=shimmer:illusion
154 Enhanced Strength | C=rise:might
155 Flesh Armor | C=ring:might
156 Graft Weapon | C=ring:stone
157 Lend Health | H=rise:heal
158 Share Strength | H=rise:might
159 Domination | C=gather:mind H=swirl:mind
160 Mass Domination | C=gather:mind H=swirl:mind
161 Psychic Crush | C=gather:psimag F=weave:psimag H=spark:psimag
162 Superior Invisibility | C=shimmer:illusion
163 Tower Of Iron Will | C=globe:stone
164 Ego Whip | C=flare:mind F=weave:mind H=spark:mind
165 Id Insinuation | C=gather:psigreen F=weave:mind H=swirl:psimag
166 Intellect Fortress | C=globe:mind
167 Mental Barrier | C=ring:mind
168 Mind Bar | C=ring:mind
169 Mind Blank | C=shimmer:mind
170 Psionic Blast | C=gather:psigreen F=bolt:psigreen H=spark:psigreen
171 Synaptic Static | C=shimmer:bolt
172 Thought Shield | C=ring:mind
173 | H=spark:arcane
174 | H=spark:arcane
175 | H=spark:acid
176 | C=gather:arcane H=puff:web D=strands:web
177 | H=sink:poison
178 | H=sink:poison
179 | H=sink:poison
180 | C=gather:necro H=sink:necro
181 | F=bolt:acid
182 | C=gather:fire B=cone:fire H=fire-hit
183 | C=gather:psigreen B=cone:psigreen H=spark:psigreen
184 | C=gather:necro H=sink:necro
185 | H=sink:poison
186 | C=gather:fire B=cone:fire H=fire-hit
187 | H=spark:acid
188 | H=spark:acid
189 | C=gather:death B=cone:death H=sink:death
190 | H=sink:poison
191 | C=gather:mind B=motes:mind H=spark:mind
192 | C=shimmer:illusion
193 | F=magic-missile-flight
194 | B=explode:fire H=fire-hit
195 | C=gather:arcane H=spark:arcane
196 | B=explode:arcane H=spark:arcane
"""

PARTS = {'C': 'Cast', 'F': 'Flight', 'B': 'Burst', 'H': 'Hit', 'D': 'Field'}


def main():
    kept()
    looks = {'_about': 'Particle looks by spell number and part (Cast, Flight, Burst, Hit, Field) and style (Pixel, Modern), '
                       'naming looks/particles/NAME.json. A spell, part or style not here keeps its original OJFF. '
                       'Written by tools/particle_looks.py.'}
    for line in filter(None, map(str.strip, SPELLS.splitlines())):
        head, parts = line.split('|')
        number = head.split()[0]
        entry = {}
        for part in parts.split():
            key, value = part.split('=')
            if ':' in value:
                archetype, theme = value.split(':')
                name = f'{archetype}-{theme}'
                if name + '.pixel' not in DEFS:
                    ARCHETYPES[archetype](T[theme], name)
            else:
                name = value
                if name + '.pixel' not in DEFS:
                    sys.exit(f'spell {number}: no look {name}')
            entry[PARTS[key]] = {'Pixel': name + '.pixel', 'Modern': name + '.modern'}
        looks[number] = entry

    folder = os.path.join(ROOT, 'particles')
    os.makedirs(folder, exist_ok=True)
    for old in os.listdir(folder):
        if old.endswith('.json') and old[:-5] not in DEFS:
            os.remove(os.path.join(folder, old))
    for name, body in sorted(DEFS.items()):
        with open(os.path.join(folder, name + '.json'), 'w') as f:
            f.write('{\n' + ',\n'.join(f'  {json.dumps(k)}: {json.dumps(v)}' for k, v in body.items()) + '\n}\n')
    with open(os.path.join(ROOT, 'particle_looks.json'), 'w') as f:
        f.write('{\n' + ',\n'.join(f'  {json.dumps(k)}: {json.dumps(v)}' for k, v in looks.items()) + '\n}\n')
    print(f'{len(DEFS)} definitions, {len(looks) - 1} spells')


if __name__ == '__main__':
    main()
