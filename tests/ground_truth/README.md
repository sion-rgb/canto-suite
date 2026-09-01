# Ground-truth corpus contract

Audio is intentionally not committed. Licensed samples are placed in `tests/audio/` and paired with UTF-8 JSONL here. Each row contains `audio`, `category`, `raw_cantonese`, and optional `speakers`. Required categories are clean HK Cantonese, Cantonese-English code-switching, noise, fast speech, meeting room, names/proper nouns, and multiple speakers. Personal meeting audio must never be added.
