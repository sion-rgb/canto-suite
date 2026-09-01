use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct TimedText {
    pub start_ms: i64,
    pub end_ms: i64,
    pub text: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Cue {
    pub start_ms: i64,
    pub end_ms: i64,
    pub lines: Vec<String>,
}

#[derive(Debug, Clone, Copy)]
pub struct SegmentationOptions {
    pub max_chars_per_line: usize,
    pub max_lines: usize,
    pub min_duration_ms: i64,
    pub max_duration_ms: i64,
    pub max_chars_per_second: f32,
}

impl Default for SegmentationOptions {
    fn default() -> Self {
        Self {
            max_chars_per_line: 18,
            max_lines: 2,
            min_duration_ms: 900,
            max_duration_ms: 7_000,
            max_chars_per_second: 14.0,
        }
    }
}

fn is_phrase_boundary(character: char) -> bool {
    matches!(character, '，' | '。' | '！' | '？' | '；' | '、' | ',' | '.' | '!' | '?' | ';')
}

fn display_width(text: &str) -> usize {
    text.chars().filter(|c| !c.is_whitespace()).count()
}

fn split_line(text: &str, limit: usize) -> Vec<String> {
    let characters: Vec<char> = text.trim().chars().collect();
    if characters.len() <= limit {
        return vec![characters.iter().collect()];
    }
    let mut lines = Vec::new();
    let mut cursor = 0;
    while cursor < characters.len() {
        let hard_end = (cursor + limit).min(characters.len());
        let mut end = hard_end;
        if hard_end < characters.len() {
            let search_start = cursor + limit / 2;
            if let Some(relative) = characters[search_start..hard_end]
                .iter()
                .rposition(|c| is_phrase_boundary(*c) || c.is_whitespace())
            {
                end = search_start + relative + 1;
            }
        }
        let line: String = characters[cursor..end].iter().collect::<String>().trim().to_owned();
        if !line.is_empty() {
            lines.push(line);
        }
        cursor = end;
    }
    lines
}

pub fn segment(items: &[TimedText], options: SegmentationOptions) -> Vec<Cue> {
    let mut cues = Vec::new();
    for item in items {
        if item.text.trim().is_empty() || item.end_ms <= item.start_ms {
            continue;
        }
        let all_lines = split_line(&item.text, options.max_chars_per_line);
        let groups: Vec<&[String]> = all_lines.chunks(options.max_lines).collect();
        let total_width = display_width(&item.text).max(1) as i64;
        let source_duration = (item.end_ms - item.start_ms).max(options.min_duration_ms);
        let mut consumed_width = 0_i64;
        for (index, group) in groups.iter().enumerate() {
            let group_width = group.iter().map(|line| display_width(line)).sum::<usize>().max(1) as i64;
            let proportional_start = item.start_ms + source_duration * consumed_width / total_width;
            consumed_width += group_width;
            let mut proportional_end = if index + 1 == groups.len() {
                item.end_ms.max(proportional_start + options.min_duration_ms)
            } else {
                item.start_ms + source_duration * consumed_width / total_width
            };
            let reading_duration = ((group_width as f32 / options.max_chars_per_second) * 1000.0) as i64;
            proportional_end = proportional_end
                .max(proportional_start + options.min_duration_ms)
                .max(proportional_start + reading_duration)
                .min(proportional_start + options.max_duration_ms);
            cues.push(Cue {
                start_ms: proportional_start,
                end_ms: proportional_end,
                lines: group.to_vec(),
            });
        }
    }
    for index in 1..cues.len() {
        if cues[index].start_ms < cues[index - 1].end_ms {
            cues[index - 1].end_ms = cues[index].start_ms.max(cues[index - 1].start_ms + 1);
        }
    }
    cues
}

fn timestamp(milliseconds: i64) -> String {
    let value = milliseconds.max(0);
    let hours = value / 3_600_000;
    let minutes = value % 3_600_000 / 60_000;
    let seconds = value % 60_000 / 1_000;
    let millis = value % 1_000;
    format!("{hours:02}:{minutes:02}:{seconds:02},{millis:03}")
}

pub fn render(cues: &[Cue]) -> String {
    let mut output = String::new();
    for (index, cue) in cues.iter().enumerate() {
        output.push_str(&(index + 1).to_string());
        output.push('\n');
        output.push_str(&format!("{} --> {}\n", timestamp(cue.start_ms), timestamp(cue.end_ms)));
        output.push_str(&cue.lines.join("\n"));
        output.push_str("\n\n");
    }
    output
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn preserves_cantonese_and_uses_valid_srt_timing() {
        let input = TimedText {
            start_ms: 1_000,
            end_ms: 9_000,
            text: "我哋今日要確認新版本，跟住安排測試同埋發佈。負責人暫時未指定。".into(),
        };
        let cues = segment(&[input], SegmentationOptions::default());
        assert!(cues.len() >= 2);
        assert!(cues.iter().all(|cue| cue.lines.iter().all(|line| display_width(line) <= 18)));
        let rendered = render(&cues);
        assert!(rendered.contains("00:00:01,000 -->"));
        assert!(rendered.contains("我哋"));
    }
}
