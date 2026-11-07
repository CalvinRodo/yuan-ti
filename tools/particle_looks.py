"""Write the engine's spell particle looks: engine/game/looks/particles/*.json and particle_looks.json,
and the light each look throws with [display] lighting on: spell_lights.json.

Every part of each of the original's 196 spell looks gets particles in both styles of
[display] spell_effects (Pixel: the region's palette; Modern: colours and glow). A look is an
archetype (how particles move: a gathering at the caster, a bolt, a rising column, a ward
ring...) in a theme (its colours). Change this file and run it rather than editing the JSON:

    uv run python3 tools/particle_looks.py

Pixel palettes use only indexes the original's spell OJFFs draw with, so they are the same in
every region (240-248, 1-5 and 11-15 turn with colour cycling, as in the originals).

A part's light is its theme's glow colour (themes without one, smoke and darkness, give none),
at the same points as its particles: a flash at the caster, a light flying with the projectile,
a flash at the burst or hit, a glow over a field while it lasts (LIGHTS).
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
    name: str = ''           # its key in T (MATERIAL)


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

for _key, _theme in T.items():
    _theme.name = _key

DEFS = {}

# Looks made whole by elemental(), which punctuate() leaves as they are.
CUSTOM = set()

# Looks punctuate() has given their beat.
BEATEN = set()


def pixel(t, **d):
    """The pixel style: the theme's palette, never blended."""
    d['Palette'] = t.palette
    if t.random:
        d['RandomColor'] = True
    return d


def modern(t, glow=None, **d):
    d['Colors'] = t.colors
    d.setdefault('Texture', 'Glow')
    d.setdefault('Scale', [0.7, 1.3])  # puffs and motes of many sizes
    if t.blend == 'Add':
        d['Blend'] = 'Add'
    if t.random:
        d['RandomColor'] = True
    if t.glow and glow:
        d['Glow'] = dict(Radius=glow[0], Color=t.glow, Alpha=glow[1])
    return d


def look(name, t, px, md):
    DEFS[name + '.pixel'] = pixel(t, **px)
    DEFS[name + '.modern'] = matter(t, name, modern(t, **md))
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
        THEME_OF[name] = theme
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
    DEFS['fireball-burst.modern'].pop('Blend', None)
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


# The modern style's elements, in layers (an effect's Layers play with it, each drawn over those before it):
# fire billows and smokes and throws embers, ice is sharp shards in a gale, and the big ones shake the view.

def layer(colors, texture='Glow', blend='Add', **d):
    """One layer of a modern look: its colours, how it is drawn, and how it moves."""
    d['Colors'] = colors
    d['Texture'] = texture
    if blend == 'Add':
        d['Blend'] = 'Add'
    return d


def layered(first, *rest, glow=None, lit=0):
    """
    A modern look of layers: the first drawn under the rest. glow is (radius, colour, alpha) of the light on the
    ground thrown by layer lit (0: the first), which it shows while that layer's particles do.
    """
    parts = [dict(first), *rest]
    if glow:
        parts[lit]['Glow'] = dict(Radius=glow[0], Color=glow[1], Alpha=glow[2])
    return {**parts[0], 'Layers': parts[1:]}


def faded(colour):
    return colour[:7] + '00'


def shock(colour, across, life=0.45, delay=0.0, alpha=0.8, blend='Add', **d):
    """A ring running out over the floor from the point."""
    return layer([colour, faded(colour)], 'Shock', blend, Burst=1, Lifetime=[life, life], Speed=[0, 0], Delay=delay,
                 Size=[across * 0.1, across * 0.6, across * 0.85, across * 0.96, across], Alpha=[alpha, alpha * 0.6, 0], **d)


def flash(colour, size, life=0.18, **d):
    """A bright pop at the point, gone in a blink."""
    return layer(['#ffffff', colour, faded(colour)], 'Glow', 'Add', **{**dict(Burst=3, Lifetime=[life * 0.7, life], Speed=[0, 0],
                 Size=[size * 0.5, size, size * 0.8], Alpha=[1, 0.6, 0]), **d})


FIRE_BILLOW = ['#fff4c0', '#ffb040', '#f06018e8', '#a02808b0', '#40100800']
FIRE_CORE = ['#fff6d0', '#ffc040', '#ff601000']
EMBERS = ['#fff0a0', '#ffa030', '#ff401000']
SMOKE = ['#4a3a3000', '#3a302ab0', '#28222090', '#18141400']
ICE = ['#ffffff', '#e8f8ff', '#b0e0ff', '#80b8f000']
FROST = ['#f0f8ff00', '#e0f0ffb0', '#c0dcf880', '#a0c4f000']


def fire_boom(where, n=1.0, s=1.0, smoke=True, shake=None, ring=None):
    """An explosion of fire: a ball of flame swelling out from the point and rolling up, a hot glow at its
    heart, flames licking over the rest of where (a shape: an area's cells, a circle about one reached),
    embers flung, a shock wave, and a column of smoke rising after."""
    k = lambda v: max(1, round(v * n))
    up = [0, -10 * s]
    ball = dict(Shape='Circle', Radius=8 * s, Offset=up)
    layers = [
        # The rest of the area catching: low flames over it.
        layer(FIRE_BILLOW, 'Smoke', 'Mix', **where, Burst=k(90), Rate=k(160), Seconds=0.3, Lifetime=[0.3, 0.7], Speed=[4, 16],
              Direction=-90, Spread=50, Gravity=[0, -30], Turbulence=40, Size=[2 * s, 4.5 * s, 2 * s], Scale=[0.7, 1.3],
              Spin=[-160, 160], Alpha=[0.9, 1, 0.5, 0], Max=k(600)),
        # The fireball: big billows thrown out from its heart, slowing, rolling up and darkening.
        layer(FIRE_BILLOW, 'Smoke', 'Mix', **ball, Radial=True, Direction=0, Spread=40, Burst=k(70), Rate=k(160), Seconds=0.15,
              Lifetime=[0.45, 0.95], Speed=[30 * s, 110 * s], Drag=3.5, Gravity=[0, -40], Turbulence=30,
              Size=[6 * s, 13 * s, 15 * s], Scale=[0.7, 1.3], Spin=[-120, 120], Alpha=[1, 1, 0.7, 0], Max=k(400)),
        # White-yellow heat inside it, burning out to orange.
        layer(['#fffbe8', '#ffe880', '#ffa830c0', '#ff602000'], 'Smoke', 'Mix', **ball, Burst=k(30), Rate=k(80), Seconds=0.15,
              Lifetime=[0.2, 0.45], Speed=[10 * s, 45 * s], Spread=180, Drag=3, Gravity=[0, -30], Size=[5 * s, 9 * s, 6 * s],
              Scale=[0.7, 1.3], Spin=[-160, 160], Alpha=[1, 1, 0], Max=k(150)),
        # Its heart: a glow, not a white-out (added onto sand, too much turns it white).
        layer(['#fff0c0', '#ff9a40', '#ff401000'], 'Glow', 'Add', Shape='Point', Offset=up, Burst=6, Lifetime=[0.18, 0.32],
              Speed=[0, 0], Size=[10 * s, 22 * s, 16 * s], Alpha=[0.7, 0.45, 0], Max=10,
              **({'Shake': dict(Strength=shake[0], Seconds=shake[1])} if shake else {})),
        # Embers flung out and falling.
        layer(EMBERS, 'Spark', 'Add', **ball, Burst=k(45), Lifetime=[0.4, 0.9], Speed=[60 * s, 140 * s], Direction=-90,
              Spread=180, Gravity=[0, 160], Drag=1.2, Size=[1.3, 0.9, 0.4], Max=k(100)),
    ]
    if ring:
        layers.append(shock('#ffc070', ring, life=0.4, alpha=0.8))
    if smoke:
        # Drawn under the flames: a column rising off it as they die.
        first = layer(SMOKE, 'Smoke', 'Mix', Shape='Circle', Radius=10 * s, Offset=[0, -14 * s], Delay=0.2, Rate=k(70), Seconds=0.5,
                      Lifetime=[1.0, 1.8], Speed=[8 * s, 22 * s], Direction=-90, Spread=35, Gravity=[0, -12], Drag=0.8,
                      Turbulence=20, Size=[6 * s, 12 * s, 18 * s], Scale=[0.7, 1.3], Spin=[-50, 50], Alpha=[0, 0.75, 0.5, 0],
                      Max=k(200))
        return layered(first, *layers, glow=(70 * s, '#ff8030', 0.55), lit=2)  # on the fireball
    return layered(*layers, glow=(70 * s, '#ff8030', 0.55), lit=1)


def hailstorm():
    """Ice Storm: a black storm cloud churning over the area, hail hammering down slantwise out of it through a
    gale of snow, shattering into splinters and powder where it lands, ice crystals forming on the ground, the
    ground rumbling under it."""
    cell = dict(Shape='Cell')
    # Hail falls at 65° (down and to the right) at about 210 pixels a second, landing on its cell after 0.35 s.
    cloud = layer(['#3a465800', '#4a5a70d0', '#56688098', '#60708800'], 'Smoke', 'Mix', **cell, Offset=[-31, -72], Burst=30,
                  Rate=70, Seconds=0.9, Lifetime=[0.8, 1.3], Speed=[4, 14], Spread=180, Turbulence=40, Size=[10, 16, 18],
                  Scale=[0.7, 1.3], Spin=[-70, 70], Alpha=[0, 0.85, 0.7, 0], Max=200)
    gale = layer(['#d0e4f800', '#c0d8f0c0', '#a8c4e890', '#90b0e000'], 'Smoke', 'Mix', **cell, Offset=[-36, -26], Rate=140,
                 Seconds=1.0, Lifetime=[0.5, 0.9], Speed=[70, 110], Direction=15, Spread=12, Turbulence=90, Size=[5, 9, 12],
                 Scale=[0.6, 1.4], Spin=[-120, 120], Alpha=[0, 0.8, 0.5, 0], Max=400)
    hail = layer(['#ffffff', '#d8f0ff', '#90c4f8'], 'Shard', 'Mix', **cell, Offset=[-31, -67], Burst=30, Rate=170, Seconds=0.9,
                 Lifetime=[0.34, 0.36], Speed=[205, 215], Direction=65, Spread=3, Size=[3.6, 4], Scale=[0.6, 1.4],
                 Alpha=[0.9, 1], Max=500)
    streaks = layer(['#ffffff80', '#d8f0ff60', '#a0d0ff00'], 'Spark', 'Add', **cell, Offset=[-31, -67], Rate=80, Seconds=0.9,
                    Lifetime=[0.3, 0.36], Speed=[230, 250], Direction=65, Spread=3, Size=[2], Alpha=[0.6, 0.6, 0], Max=300)
    crystals = layer(['#f0faff', '#b8e0ff', '#80b8f0'], 'Shard', 'Mix', **cell, Delay=0.4, Rate=45, Seconds=0.8,
                     Lifetime=[0.9, 1.4], Speed=[0, 0], Direction=-90, Spread=0, Size=[0, 2.6, 2.6, 0], Scale=[0.6, 1.4],
                     Spin=[-8, 8], Alpha=[1], Max=200)
    splinters = layer(ICE, 'Shard', 'Mix', **cell, Delay=0.34, Rate=220, Seconds=0.9, Lifetime=[0.25, 0.5], Speed=[40, 100],
                      Direction=-90, Spread=75, Gravity=[0, 300], Size=[2, 1.4], Scale=[0.6, 1.3], Spin=[-400, 400], Max=500,
                      Shake=dict(Strength=2, Seconds=0.9))
    powder = layer(['#ffffffd0', '#e0f0ffa0', '#c0dcf800'], 'Smoke', 'Mix', **cell, Delay=0.34, Rate=110, Seconds=0.9,
                   Lifetime=[0.3, 0.6], Speed=[10, 26], Direction=-90, Spread=80, Drag=2.5, Size=[2.5, 5.5, 7], Scale=[0.7, 1.3],
                   Spin=[-120, 120], Max=300)
    frost = shock('#a8dcff', 120, life=1.2, delay=0.35, alpha=0.6, blend='Add')
    return layered(frost, crystals, cloud, gale, hail, streaks, powder, splinters, glow=(80, '#a0d8ff', 0.5), lit=4)


def elemental():
    """The modern style's fire, ice, lightning and stone (over the archetypes' and kept() looks)."""
    for name in ['explode-fire', 'blast-fire', 'cone-fire', 'fall-fire', 'fall-cold', 'spark-cold', 'shards-cold', 'blast-stone']:
        if name + '.pixel' not in DEFS:
            archetype, theme = name.split('-', 1)
            ARCHETYPES[archetype](T[theme], name)
    hit = dict(Shape='Circle', Radius=6, Offset=[0, -8])

    # Fireball: the great one, filling its area, shaking the view.
    DEFS['fireball-burst.modern'] = fire_boom(dict(Shape='Cell'), n=1.0, s=1.2, shake=(3.5, 0.5), ring=150)
    DEFS['explode-fire.modern'] = fire_boom(dict(Shape='Cell'), n=0.8, s=1.0, shake=(2.5, 0.4), ring=110)
    # A meteor striking one: a small explosion on it.
    DEFS['blast-fire.modern'] = fire_boom(hit, n=0.25, s=0.8, shake=(1.2, 0.25), ring=50)
    # The fireball in flight: a roiling ball of flame trailing smoke.
    DEFS['fire-flight.modern'] = layered(
        layer(SMOKE, 'Smoke', 'Mix', Rate=90, Lifetime=[0.4, 0.8], Speed=[2, 8], Spread=180, Gravity=[0, -14], Turbulence=20,
              Size=[2, 5, 7], Scale=[0.7, 1.3], Spin=[-90, 90], Alpha=[0, 0.5, 0.3, 0], Max=300),
        layer(FIRE_BILLOW, 'Smoke', 'Mix', Rate=200, Lifetime=[0.15, 0.4], Speed=[4, 20], Spread=180, Gravity=[0, -30], Drag=1,
              Turbulence=40, Size=[4.5, 3.5, 1.5], Scale=[0.7, 1.3], Spin=[-200, 200], Alpha=[1, 0.9, 0], Max=500),
        layer(FIRE_CORE, 'Glow', 'Add', Rate=120, Lifetime=[0.08, 0.2], Speed=[0, 8], Spread=180, HeadSize=9, Size=[4, 2],
              Alpha=[1, 0], Max=200),
        layer(EMBERS, 'Spark', 'Add', Rate=40, Lifetime=[0.3, 0.6], Speed=[20, 50], Spread=180, Gravity=[0, 80], Size=[1.2, 0.5],
              Max=100),
        glow=(44, '#ff8030', 0.6), lit=1)
    # Burning Hands and the like: a roaring gout of flame from the hands, embers and smoke after.
    cone = dict(Shape='Cone', Direction=0)
    DEFS['cone-fire.modern'] = layered(
        layer(SMOKE, 'Smoke', 'Mix', **cone, Spread=18, Delay=0.15, Rate=160, Seconds=0.35, Lifetime=[0.6, 1.0], Speed=[40, 80],
              Drag=1.6, Gravity=[0, -20], Turbulence=40, Size=[3, 6, 9], Scale=[0.7, 1.3], Spin=[-90, 90], Alpha=[0, 0.5, 0.3, 0],
              Max=400),
        layer(FIRE_BILLOW, 'Smoke', 'Mix', **cone, Spread=20, Burst=60, Rate=520, Seconds=0.4, Lifetime=[0.25, 0.6],
              Speed=[70, 140], Drag=2.2, Gravity=[0, -20], Turbulence=90, Size=[1.5, 4.5, 7], Scale=[0.7, 1.3], Spin=[-220, 220],
              Alpha=[1, 0.95, 0.5, 0], Max=1200),
        layer(FIRE_CORE, 'Glow', 'Add', **cone, Spread=12, Burst=30, Rate=300, Seconds=0.35, Lifetime=[0.12, 0.3], Speed=[90, 160],
              Drag=2, Size=[2, 3.5, 1], Alpha=[1, 0.8, 0], Max=500),
        layer(EMBERS, 'Spark', 'Add', **cone, Spread=26, Rate=120, Seconds=0.4, Lifetime=[0.3, 0.7], Speed=[110, 190], Drag=1.5,
              Gravity=[0, 60], Size=[1.4, 0.5], Max=200),
        glow=(40, '#ff8030', 0.5), lit=1)
    # A hit by fire: flames lick up the one reached.
    DEFS['fire-hit.modern'] = layered(
        layer(FIRE_BILLOW, 'Smoke', 'Mix', Shape='Circle', Radius=6, Burst=24, Rate=60, Seconds=0.35, Lifetime=[0.25, 0.55],
              Speed=[10, 30], Direction=-90, Spread=40, Gravity=[0, -40], Turbulence=60, Size=[2, 4, 1.5], Scale=[0.7, 1.3],
              Spin=[-200, 200], Alpha=[1, 0.9, 0], Max=200),
        flash('#ff9040', 12, Offset=[0, -10]),
        layer(EMBERS, 'Spark', 'Add', Shape='Circle', Radius=6, Offset=[0, -8], Burst=14, Lifetime=[0.3, 0.6], Speed=[30, 70],
              Direction=-90, Spread=70, Gravity=[0, 120], Size=[1.2, 0.5], Max=60),
        glow=(28, '#ff8030', 0.55))
    # Wall of Fire: tall flames roiling over its cells, embers and smoke going up.
    DEFS['wall-of-fire-field.modern'] = layered(
        layer(SMOKE, 'Smoke', 'Mix', Shape='Cell', Offset=[0, -14], Rate=3, Lifetime=[1.0, 1.6], Speed=[8, 16], Direction=-90,
              Spread=20, Gravity=[0, -10], Turbulence=20, Size=[4, 7, 10], Scale=[0.7, 1.3], Spin=[-60, 60], Alpha=[0, 0.4, 0.25, 0],
              Max=500),
        layer(['#fff0b0', '#ffb030', '#ff5010e0', '#a0200890', '#40100000'], 'Smoke', 'Mix', Shape='Cell', Rate=16,
              Lifetime=[0.35, 0.75], Speed=[14, 34], Direction=-90, Spread=15, Gravity=[0, -16], Turbulence=60, Size=[5, 4, 1.5],
              Scale=[0.7, 1.3], Spin=[-160, 160], Alpha=[0.9, 1, 0.6, 0], Max=1200),
        layer(EMBERS, 'Spark', 'Add', Shape='Cell', Rate=2, Lifetime=[0.5, 1.0], Speed=[20, 50], Direction=-90, Spread=30,
              Turbulence=80, Size=[1.2, 0.5], Max=200),
        glow=(40, '#ff8030', 0.5), lit=1)
    # Flame Strike: a pillar of fire down from the sky, bursting where it lands.
    strike = fire_boom(dict(Shape='Cell'), n=0.6, s=0.9, shake=(2.5, 0.4), ring=100, smoke=True)
    for part in strike['Layers']:
        part['Delay'] = part.get('Delay', 0) + 0.25
    strike['Delay'] = strike.get('Delay', 0) + 0.25
    strike['Layers'].insert(0, layer(['#fff8e0', '#ffd060', '#ff702000'], 'Spark', 'Add', Shape='Cell', Offset=[0, -60],
                                     Burst=40, Rate=280, Seconds=0.3, Lifetime=[0.24, 0.26], Speed=[230, 250], Direction=90,
                                     Spread=2, Size=[2.5, 2], Max=600))
    DEFS['fall-fire.modern'] = strike

    # Ice Storm.
    DEFS['fall-cold.modern'] = hailstorm()
    # Cone of Cold: a freezing blast of mist, shards and rime.
    DEFS['cone-of-cold-burst.modern'] = layered(
        layer(FROST, 'Smoke', 'Mix', **cone, Spread=22, Burst=40, Rate=300, Seconds=0.45, Lifetime=[0.4, 0.8], Speed=[60, 120],
              Drag=1.8, Turbulence=60, Size=[2, 6, 9], Scale=[0.7, 1.3], Spin=[-120, 120], Alpha=[0, 0.8, 0.5, 0], Max=900),
        layer(ICE, 'Shard', 'Mix', **cone, Spread=20, Burst=60, Rate=420, Seconds=0.4, Lifetime=[0.25, 0.55], Speed=[120, 200],
              Drag=1.2, Size=[2.2, 1.6], Scale=[0.6, 1.4], Max=900),
        layer(['#ffffff', '#c8f0ff', '#80c0ff00'], 'Glow', 'Add', **cone, Spread=10, Burst=20, Rate=200, Seconds=0.35,
              Lifetime=[0.15, 0.3], Speed=[100, 170], Size=[2, 3, 1], Alpha=[1, 0.7, 0], Max=300,
              Shake=dict(Strength=1.2, Seconds=0.35)),
        glow=(36, '#a0d8ff', 0.45))
    # A hit by cold: shards burst off the one reached in a puff of frost.
    shatter = lambda n, size: [
        layer(FROST, 'Smoke', 'Mix', Shape='Circle', Radius=6, Offset=[0, -12], Burst=n, Lifetime=[0.35, 0.7], Speed=[6, 20],
              Spread=180, Drag=2, Size=[2, 5, 7], Scale=[0.7, 1.3], Spin=[-120, 120], Alpha=[0, 0.8, 0], Max=100),
        flash('#a0d8ff', 10, Offset=[0, -12]),
        layer(ICE, 'Shard', 'Mix', Shape='Circle', Radius=4, Offset=[0, -12], Burst=n * 2, Lifetime=[0.25, 0.55],
              Speed=[30, 80], Spread=180, Gravity=[0, 160], Drag=1.5, Size=[size, size * 0.6], Scale=[0.6, 1.4], Max=200)]
    DEFS['cold-hit.modern'] = layered(*shatter(12, 1.8), glow=(24, '#a0d8ff', 0.45))
    DEFS['spark-cold.modern'] = layered(*shatter(10, 1.6), glow=(26, '#a0d8ff', 0.5))
    # Wall of Ice: its face glinting, rime drifting off it.
    DEFS['shards-cold.modern'] = layered(
        layer(FROST, 'Smoke', 'Mix', Shape='Cell', Rate=2, Lifetime=[1.0, 1.6], Speed=[2, 6], Direction=90, Spread=40,
              Size=[4, 7, 9], Scale=[0.7, 1.3], Spin=[-40, 40], Alpha=[0, 0.5, 0], Max=300),
        layer(ICE, 'Shard', 'Mix', Shape='Cell', Offset=[0, -6], Rate=4, Lifetime=[0.6, 1.1], Speed=[0, 2], Spread=180,
              Size=[1, 2.5, 1], Scale=[0.6, 1.4], Spin=[-30, 30], Alpha=[0, 1, 0], Max=300),
        layer(['#ffffff', '#d8f6ff', '#90c8ff00'], 'Spark', 'Add', Shape='Cell', Offset=[0, -6], Rate=3, Lifetime=[0.3, 0.6],
              Speed=[2, 8], Direction=-90, Spread=10, Size=[0.5, 3, 0.5], Alpha=[0, 1, 0], Max=200),
        glow=(30, '#a0d8ff', 0.25))

    # Lightning Bolt: a blinding flash along its line, the view jolting.
    bolt = DEFS['lightning-burst.modern']
    DEFS['lightning-burst.modern'] = layered(
        bolt, layer(['#ffffff', '#c0f8ff', '#5090ff00'], 'Glow', 'Add', Shape='Line', Burst=40, Lifetime=[0.06, 0.16],
                    Speed=[0, 0], Size=[10, 6], Alpha=[1, 0], Max=60, Shake=dict(Strength=2.5, Seconds=0.3)))

    # Stone striking: rubble flung, a cloud of dust, the ground jolting.
    DEFS['blast-stone.modern'] = layered(
        layer(['#c8b8a000', '#b0a08ca0', '#8c806c80', '#60584800'], 'Smoke', 'Mix', **hit, Burst=20, Rate=40, Seconds=0.2,
              Lifetime=[0.6, 1.1], Speed=[10, 30], Spread=180, Drag=2, Gravity=[0, -8], Size=[3, 7, 9], Scale=[0.7, 1.3],
              Spin=[-80, 80], Alpha=[0, 0.8, 0.5, 0], Max=150),
        layer(['#d8d0c0', '#a09080', '#605040'], 'Shard', 'Mix', **hit, Burst=40, Lifetime=[0.4, 0.8], Speed=[40, 110],
              Direction=-90, Spread=100, Gravity=[0, 260], Drag=0.5, Size=[2, 1.6], Scale=[0.6, 1.5], Spin=[-500, 500], Max=100,
              Shake=dict(Strength=1.5, Seconds=0.25)),
        shock('#d8c8a8', 50, life=0.35, alpha=0.6, blend='Mix'))


# What each theme is made of in the modern style: its particles take that stuff's look (flame billows, ice
# shards, dust, rubble, leaves, gas...) and more layers come with them (embers and smoke, frost, crackle,
# rays of light, drips, grit), so every look keeps its archetype's motion but reads as its element.
MATERIAL = {
    'fire': 'flame', 'cold': 'ice', 'holy': 'radiant', 'heal': 'radiant', 'light': 'radiant',
    'dark': 'dark', 'shadow': 'dark', 'fear': 'dark', 'necro': 'gas', 'poison': 'gas', 'fog': 'gas',
    'sand': 'dust', 'stone': 'stone', 'water': 'liquid', 'acid': 'liquid', 'blood': 'liquid', 'grease': 'liquid',
    'nature': 'nature', 'air': 'air', 'chaos': 'chaos', 'web': None, 'insects': None,
}

# Where a layer starts and how it flies, taken from the look it is added to.
PLACE = ('Shape', 'Radius', 'Offset', 'Seconds', 'Heads', 'Wander', 'Stagger', 'MinSeconds')


def matter(t, name, d):
    """Look d (a modern archetype's) made of its theme's material, with that material's layers."""
    material = MATERIAL.get(t.name, 'energy')
    if material is None or d.get('Texture') == 'Pixel':
        return d
    place = {k: d[k] for k in PLACE if k in d}
    if place.get('Shape') == 'Cone':  # a cone's layers spray its way too
        place.update(Direction=d.get('Direction', 0), Spread=d.get('Spread', 20))
    aimed = place.get('Shape') == 'Cone'

    def more(share, colors, texture, blend, **motion):
        """A layer giving off share of the look's particles."""
        burst, rate = round(d.get('Burst', 0) * share), d.get('Rate', 0) * share
        if burst == 0 and rate == 0:
            return None
        if aimed:
            for k in ('Direction', 'Spread', 'Gravity'):
                motion.pop(k, None)
            motion['Speed'] = [s * 0.8 for s in d.get('Speed', [0, 0])]
        return layer(colors, texture, blend, **place, **({'Burst': burst} if burst else {}), **({'Rate': rate} if rate else {}),
                     Max=max(40, round(d.get('Max', 300) * share) + 20), **motion)

    spin = [-160, 160]
    hue = t.colors
    blend = t.blend
    turn = dict(Spin=spin, Turbulence=d.get('Turbulence', 0) + 25)
    if material == 'flame':
        d.update(Colors=FIRE_BILLOW, Texture='Smoke', **turn)
        d.pop('Blend', None)
        rest = [more(0.15, SMOKE, 'Smoke', 'Mix', Delay=0.1, Lifetime=[0.6, 1.1], Speed=[4, 12], Direction=-90, Spread=40,
                     Gravity=[0, -10], Size=[3, 6, 8], Scale=[0.7, 1.3], Spin=[-60, 60], Alpha=[0, 0.5, 0.3, 0]),
                more(0.3, EMBERS, 'Spark', 'Add', Lifetime=[0.3, 0.7], Speed=[20, 60], Direction=-90, Spread=60,
                     Gravity=[0, 90], Size=[1.2, 0.5])]
    elif material == 'ice':
        d.update(**turn)
        rest = [more(0.15, FROST, 'Smoke', 'Mix', Lifetime=[0.5, 0.9], Speed=[2, 8], Spread=180, Size=[3, 6, 8], Scale=[0.7, 1.3],
                     Spin=[-60, 60], Alpha=[0, 0.7, 0]),
                more(0.35, ICE, 'Shard', 'Mix', Lifetime=[0.3, 0.6], Speed=[10, 40], Spread=180, Gravity=[0, 60], Size=[1.8, 1.1],
                     Scale=[0.6, 1.4], Spin=[-300, 300])]
    elif material == 'radiant':
        rest = [more(0.1, hue, 'Spark', 'Add', Lifetime=[0.4, 0.8], Speed=[12, 26], Direction=-90, Spread=5,
                     Size=[0.5, 5, 0.5], Alpha=[0, 0.8, 0]),
                more(0.12, ['#ffffff', hue[1]], 'Glow', 'Add', Lifetime=[0.25, 0.6], Speed=[0, 6], Spread=180,
                     Size=[0, 2.6, 0], Scale=[0.6, 1.4])]
    elif material == 'gas':
        d.update(Texture='Smoke', **turn)
        rest = [more(0.2, hue, 'Smoke', blend, Delay=0.1, Lifetime=[0.8, 1.4], Speed=[2, 8], Spread=180, Drag=0.6, Turbulence=20,
                     Size=[s * 1.8 for s in d.get('Size', [2])], Scale=[0.7, 1.3], Spin=[-50, 50], Alpha=[0, 0.6, 0.4, 0])]
    elif material == 'dark':
        d.update(Texture='Smoke', **turn)
        rest = [more(0.3, hue, 'Spark', blend, Lifetime=[0.3, 0.6], Speed=[20, 45], Spread=180, Pull=60, Turbulence=60,
                     Size=[2.5, 1.5, 0.5], Alpha=[0, 1, 0]),
                more(0.15, hue, 'Smoke', blend, Delay=0.08, Lifetime=[0.6, 1.0], Speed=[2, 8], Spread=180, Turbulence=30,
                     Size=[4, 8, 10], Scale=[0.7, 1.3], Spin=[-50, 50], Alpha=[0, 0.6, 0])]
    elif material == 'dust':
        d.update(Texture='Smoke', **turn)
        rest = [more(0.35, hue, 'Pixel', 'Mix', Lifetime=[0.3, 0.6], Speed=[20, 50], Direction=-90, Spread=70, Gravity=[0, 150],
                     Size=[1.2])]
    elif material == 'stone':
        d.update(Texture='Shard', Spin=[-300, 300])
        rest = [more(0.2, ['#c8b8a000', '#b0a08ca0', '#8c806c80', '#60584800'], 'Smoke', 'Mix', Lifetime=[0.5, 1.0],
                     Speed=[4, 14], Spread=180, Drag=1.5, Size=[3, 6, 8], Scale=[0.7, 1.3], Spin=[-60, 60], Alpha=[0, 0.7, 0])]
    elif material == 'liquid':
        rest = [more(0.35, hue, 'Glow', blend, Lifetime=[0.3, 0.6], Speed=[30, 70], Direction=-90, Spread=55, Gravity=[0, 220],
                     Size=[1.5, 1], Scale=[0.6, 1.4])]
        if t.name == 'acid':
            rest.append(more(0.15, ['#d0ff8000', '#a0e050a0', '#60a03060', '#30601000'], 'Smoke', 'Mix', Lifetime=[0.6, 1.0],
                             Speed=[6, 14], Direction=-90, Spread=30, Turbulence=30, Size=[2, 5, 7], Scale=[0.7, 1.3],
                             Spin=[-60, 60], Alpha=[0, 0.7, 0]))
    elif material == 'nature':
        d.update(Texture='Shard', **turn)
        rest = [more(0.2, ['#f0ffa0', '#b0e060', '#70a03000'], 'Glow', 'Add', Lifetime=[0.4, 0.9], Speed=[2, 10], Spread=180,
                     Turbulence=30, Size=[0, 2, 0])]
    elif material == 'air':
        d.update(Texture='Smoke', **turn)
        rest = [more(0.3, hue, 'Spark', blend, Lifetime=[0.2, 0.4], Speed=[60, 110], Spread=180, Turbulence=120,
                     Size=[3, 0.5], Alpha=[0, 0.8, 0])]
    elif material == 'chaos':
        rest = [more(0.25, hue, 'Shard', blend, RandomColor=True, Lifetime=[0.4, 0.8], Speed=[15, 45], Spread=180,
                     Turbulence=60, Size=[1.8, 1.2], Scale=[0.6, 1.4], Spin=[-400, 400])]
    else:  # energy: a crackle of sparks about it
        rest = [more(0.25, hue, 'Spark', blend, Lifetime=[0.08, 0.2], Speed=[25, 70], Spread=180, Turbulence=150,
                     Size=[1.6, 0.5])]
    rest = [r for r in rest if r]
    if rest:
        d['Layers'] = rest
    return d


def made_whole(before):
    """The modern looks elemental() replaced (CUSTOM)."""
    CUSTOM.update(n[:-len('.modern')] for n, d in DEFS.items() if n.endswith('.modern') and before.get(n) is not d)


def punctuate(name, part, theme):
    """
    Every other modern burst, hit and cast lands with a beat: a pop of light (themes that glow) and a ring
    run out over the floor (a burst's with a jolt), unless elemental() made it whole; once a look, whatever parts use it.
    """
    body = DEFS[name + '.modern']
    t = T[theme]
    if name in CUSTOM or name in BEATEN or part not in ('Cast', 'Burst', 'Hit') or body.get('Shape') in ('Cone', 'Line') \
            or 'Heads' in body:
        return
    colour = t.glow or t.colors[1]
    blend = 'Add' if t.glow else 'Mix'
    offset = body.get('Offset', [0, 0])
    if part == 'Burst':
        rest = [shock(colour[:7], 120, life=0.5, alpha=0.8, blend=blend)]
        if t.glow:
            rest.append(flash(colour[:7], 14, life=0.22, Shape='Cell', Burst=6, Alpha=[0.6, 0.3, 0],
                              Shake=dict(Strength=1.5, Seconds=0.3)))
    elif part == 'Hit':
        rest = [shock(colour[:7], 36, life=0.35, alpha=0.6, blend=blend)]
        if t.glow:
            rest.append(flash(colour[:7], 10, Offset=[0, min(offset[1], -10)]))
    else:
        rest = [shock(colour[:7], 44, life=0.5, alpha=0.6, blend=blend)]
    body['Layers'] = body.get('Layers', []) + rest
    BEATEN.add(name)


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

# The hand-made looks' themes (kept() fills it); an archetype's look is named archetype-theme.
THEME_OF = {}

# Each part's light: map pixels it reaches, its strength, how long it fades (0: while the flight or field lasts).
LIGHTS = {
    'Cast': dict(Radius=40, Strength=0.9, Seconds=0.6),
    'Flight': dict(Radius=40, Strength=1.0),
    'Burst': dict(Radius=96, Strength=1.3, Seconds=0.9),
    'Hit': dict(Radius=36, Strength=0.9, Seconds=0.5),
    'Field': dict(Radius=56, Strength=0.8, Flicker=0.25),
}


def light(part, theme):
    """The light a part in a theme throws, or None (a theme without a glow)."""
    glow = T[theme].glow
    if glow is None:
        return None
    body = dict(Color=glow[:7], **LIGHTS[part])
    if theme == 'fire':
        body['Flicker'] = max(body.get('Flicker', 0), 0.2)
    return body


def tidy(value):
    """Numbers worked out from others rounded (55.199999999999996 → 55.2)."""
    if isinstance(value, float):
        return round(value, 3)
    if isinstance(value, list):
        return [tidy(v) for v in value]
    if isinstance(value, dict):
        return {k: tidy(v) for k, v in value.items()}
    return value


def field(key, value):
    """A definition's field as written: its layers one to a line."""
    if key == 'Layers':
        return '[\n' + ',\n'.join(f'    {json.dumps(tidy(layer))}' for layer in value) + '\n  ]'
    return json.dumps(tidy(value))


def main():
    kept()
    before = dict(DEFS)
    elemental()
    made_whole(before)
    looks = {'_about': 'Particle looks by spell number and part (Cast, Flight, Burst, Hit, Field) and style (Pixel, Modern), '
                       'naming looks/particles/NAME.json. A spell, part or style not here keeps its original OJFF. '
                       'Written by tools/particle_looks.py.'}
    lights = {'_about': 'The light each spell part throws with [display] lighting on, by spell number and part: its colour, '
                        'Radius (map pixels), Strength, Flicker and Seconds (0: while the flight or field lasts). '
                        'Written by tools/particle_looks.py.'}
    for line in filter(None, map(str.strip, SPELLS.splitlines())):
        head, parts = line.split('|')
        number = head.split()[0]
        entry = {}
        lit = {}
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
            theme = value.split(':')[1] if ':' in value else THEME_OF.get(name)
            if theme:
                punctuate(name, PARTS[key], theme)
            if theme and (body := light(PARTS[key], theme)):
                lit[PARTS[key]] = body
        looks[number] = entry
        if lit:
            lights[number] = lit

    folder = os.path.join(ROOT, 'particles')
    os.makedirs(folder, exist_ok=True)
    for old in os.listdir(folder):
        if old.endswith('.json') and old[:-5] not in DEFS:
            os.remove(os.path.join(folder, old))
    for name, body in sorted(DEFS.items()):
        with open(os.path.join(folder, name + '.json'), 'w') as f:
            f.write('{\n' + ',\n'.join(f'  {json.dumps(k)}: {field(k, v)}' for k, v in body.items()) + '\n}\n')
    with open(os.path.join(ROOT, 'particle_looks.json'), 'w') as f:
        f.write('{\n' + ',\n'.join(f'  {json.dumps(k)}: {json.dumps(v)}' for k, v in looks.items()) + '\n}\n')
    with open(os.path.join(ROOT, 'spell_lights.json'), 'w') as f:
        f.write('{\n' + ',\n'.join(f'  {json.dumps(k)}: {json.dumps(v)}' for k, v in lights.items()) + '\n}\n')
    print(f'{len(DEFS)} definitions, {len(looks) - 1} spells, {len(lights) - 1} with light')


if __name__ == '__main__':
    main()
