# Suggestion Category UX Checklist

This tracks which suggestion categories still use the default row-right UX (stars/percent/FC text and/or instrument chips), vs which ones have custom per-category UX.

## Legend
- ✅ Customized (special header/row-right behavior)
- ⬜ Default (untouched)

## Fixed-key categories
- ✅ `near_fc_any` (row-right instrument icon)
- ✅ `near_fc_any_decade_XX` (row-right instrument icon)

- ✅ `near_fc_relaxed` (row-right instrument icon)
- ✅ `near_fc_relaxed_decade_XX` (row-right instrument icon)

- ✅ `almost_six_star` (row-right instrument icon)
- ✅ `almost_six_star_decade_XX` (row-right instrument icon)

- ✅ `more_stars` (row-right instrument icon)
- ✅ `more_stars_decade_XX` (row-right instrument icon)

- ✅ `unfc_{instrument}` (header icon; row-right percent text)
- ✅ `unfc_{instrument}_decade_XX` (header icon; row-right percent text)

- ✅ `variety_pack` (row-right hidden)

- ✅ `first_plays_mixed` (row-right random unplayed-instrument icon; repeats allowed with different icon)
- ✅ `first_plays_mixed_decade_XX` (row-right random unplayed-instrument icon; repeats allowed with different icon)

- ✅ `unplayed_{instrument}` (header icon)
- ✅ `unplayed_{instrument}_decade_XX` (header icon)
- ✅ `unplayed_any` (row-right hidden; no header icon)
- ✅ `unplayed_any_decade_XX` (row-right hidden; no header icon)

- ✅ `star_gains` (row-right instrument icon + centered stars row)
- ✅ `star_gains_decade_XX` (row-right instrument icon + centered stars row)

## Dynamic-key categories
- ✅ `artist_sampler_{artistName}` (row-right hidden)
- ✅ `artist_unplayed_{artistKey}` (row-right hidden)
- ✅ `samename_{title}` (row-right hidden)
- ✅ `samename_nearfc_{title}` (row-right instrument icon)

## Notes / Next UX candidates
- `star_gains*`: consider a custom right-side cue (e.g., show target: “+★”/progress to 6★, or show best instrument icon).
- `unplayed_any*`: consider showing a random unplayed instrument icon (like `first_plays_mixed`) or a neutral “Any” treatment.
- `artist_sampler_*`: consider showing “played on X instruments” or just hide right side in compact mode.
- `samename_*` and `samename_nearfc_*`: consider a distinctive row-right marker (e.g., a small “= name” chip) or keep default.
