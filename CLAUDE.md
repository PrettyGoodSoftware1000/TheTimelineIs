# Working on this project

## How to answer me

Short. Plain words. Say the thing, then stop.

- No recaps of what you just did unless something surprising happened.
- No lists of options I didn't ask for.
- Explain like `Explanations.md` does — a few lines, no jargon unless defined.
- Long output is fine when it's *work* (code, a file). Not when it's talk.

## Rules that don't change

- `Core/` must never touch `System.IO` for game assets — `TitleContainer.OpenStream`
  only, so it can run on a tablet later. Anything file- or hardware-bound goes
  in `Desktop/`.
- Content mistakes must never fail silently. Bad `.txt` lines report the file
  and line number at startup.
- Check claims against the code before making them. Run it if you can.
