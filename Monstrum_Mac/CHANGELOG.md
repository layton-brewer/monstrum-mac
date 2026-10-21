# Changelog

## 1.0.3

- Hit effects, blood and other particles no longer show up as solid squares. See-through or cut-out
  particle, trail and line materials whose shader has no Metal code now move to the game's own legacy
  particle shaders (Alpha Blended, or Additive for additive ones) instead of the lit Standard shader, which
  ignores particle vertex colours. Every other material is handled as before.

## 1.0.2

- No more stutter after loading into a world on Apple Silicon. Shader test draws are now read back from the GPU
  asynchronously, a few per frame, instead of each one stalling a frame until the GPU caught up (about 50 ms
  each when the game runs natively). The test and its verdicts are unchanged.

## 1.0.1

- Fewer background material checks once the world has settled: the wait between checks now backs off
  to 10 minutes (setting `MaxSweepInterval`) instead of 1 minute. Mod bundle loads and new scenes still
  trigger a check right away, so newly loaded gear is fixed as before.

## 1.0.0

- First release: Therzie's Monstrum, Armory, Warfare and Wizardry with the Monstrum Mac fix, so their creatures,
  gear and effects draw properly on a Mac.
