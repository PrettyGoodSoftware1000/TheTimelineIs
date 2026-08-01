Card sounds go here as WAV files (PCM .wav; MP3/OGG are not supported).

Reference them from Cards.txt:
  Sounds: Activation[FireballCast.wav], 0.6, Hit[FireballBoom.wav]

Activation plays the moment the card is clicked. The number is how long the
walk or projectile takes to reach the target, in seconds. Hit plays when it
lands. Any part may be left out.

A sound file that doesn't exist is reported once in the console and then
ignored - the timing still works, it's just silent.
