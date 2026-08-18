//! Minimal SVG path parser.
//!
//! Handles exactly the subset the vendored Fluent glyphs use: absolute
//! `M`, `L`, `C`, `Z`, `V`, `H`. Anything else is rejected. A loud error
//! beats a silently misdrawn icon.

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Point {
    pub x: f32,
    pub y: f32,
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub enum Segment {
    Line(Point),
    /// Two control points then the end point.
    Cubic(Point, Point, Point),
}

#[derive(Debug, Clone, PartialEq)]
pub struct Figure {
    pub start: Point,
    pub segments: Vec<Segment>,
}

/// Splits path data into (command letter, number run) pairs. Numbers may butt
/// directly against letters and against each other via a leading sign.
fn tokenize(d: &str) -> Result<Vec<(Option<char>, Vec<f32>)>, String> {
    let mut tokens: Vec<(Option<char>, Vec<f32>)> = Vec::new();
    let mut pending_cmd: Option<char> = None;
    let mut pending: Vec<f32> = Vec::new();
    let mut chars = d.chars().peekable();

    while let Some(&c) = chars.peek() {
        if c.is_ascii_alphabetic() {
            if pending_cmd.is_some() || !pending.is_empty() {
                tokens.push((pending_cmd, std::mem::take(&mut pending)));
            }
            pending_cmd = Some(c);
            chars.next();
        } else if c.is_whitespace() || c == ',' {
            chars.next();
        } else {
            let mut s = String::new();
            if c == '-' || c == '+' {
                s.push(c);
                chars.next();
            }
            while let Some(&c2) = chars.peek() {
                if c2.is_ascii_digit() || c2 == '.' {
                    s.push(c2);
                    chars.next();
                } else {
                    break;
                }
            }
            if s.is_empty() || s == "-" || s == "+" || s == "." {
                return Err(format!("unexpected character '{c}'"));
            }
            pending.push(s.parse::<f32>().map_err(|e| e.to_string())?);
        }
    }
    if pending_cmd.is_some() || !pending.is_empty() {
        tokens.push((pending_cmd, pending));
    }
    Ok(tokens)
}

/// The point a `V`/`H` (or a relative command, were one supported) would
/// continue from: the figure's start until a segment is appended, then that
/// segment's endpoint.
fn current_point(f: &Figure) -> Point {
    match f.segments.last() {
        None => f.start,
        Some(Segment::Line(p)) => *p,
        Some(Segment::Cubic(_, _, p)) => *p,
    }
}

pub fn parse_path(d: &str) -> Result<Vec<Figure>, String> {
    let mut figures: Vec<Figure> = Vec::new();
    let mut current: Option<Figure> = None;
    let mut command: Option<char> = None;

    for (cmd, nums) in tokenize(d)? {
        if let Some(c) = cmd {
            command = Some(c);
        }
        let c = command.ok_or_else(|| "path does not begin with a command".to_string())?;
        match c {
            'M' => {
                if nums.len() < 2 || nums.len() % 2 != 0 {
                    return Err("M needs pairs of coordinates".into());
                }
                if let Some(f) = current.take() {
                    figures.push(f);
                }
                let mut fig = Figure {
                    start: Point { x: nums[0], y: nums[1] },
                    segments: Vec::new(),
                };
                // Extra pairs after a moveto are implicit linetos.
                for pair in nums[2..].chunks(2) {
                    fig.segments
                        .push(Segment::Line(Point { x: pair[0], y: pair[1] }));
                }
                current = Some(fig);
                // A bare coordinate run after M continues as L.
                command = Some('L');
            }
            'L' => {
                let f = current.as_mut().ok_or("L before M")?;
                if nums.is_empty() || nums.len() % 2 != 0 {
                    return Err("L needs pairs of coordinates".into());
                }
                for pair in nums.chunks(2) {
                    f.segments.push(Segment::Line(Point { x: pair[0], y: pair[1] }));
                }
            }
            'C' => {
                let f = current.as_mut().ok_or("C before M")?;
                if nums.is_empty() || nums.len() % 6 != 0 {
                    return Err("C needs groups of six coordinates".into());
                }
                for g in nums.chunks(6) {
                    f.segments.push(Segment::Cubic(
                        Point { x: g[0], y: g[1] },
                        Point { x: g[2], y: g[3] },
                        Point { x: g[4], y: g[5] },
                    ));
                }
            }
            'V' => {
                let f = current.as_mut().ok_or("V before M")?;
                if nums.is_empty() {
                    return Err("V needs at least one coordinate".into());
                }
                for &y in &nums {
                    let x = current_point(f).x;
                    f.segments.push(Segment::Line(Point { x, y }));
                }
            }
            'H' => {
                let f = current.as_mut().ok_or("H before M")?;
                if nums.is_empty() {
                    return Err("H needs at least one coordinate".into());
                }
                for &x in &nums {
                    let y = current_point(f).y;
                    f.segments.push(Segment::Line(Point { x, y }));
                }
            }
            'Z' => {
                if !nums.is_empty() {
                    return Err("Z takes no coordinates".into());
                }
                if let Some(f) = current.take() {
                    figures.push(f);
                }
            }
            other => return Err(format!("unsupported path command '{other}'")),
        }
    }

    if let Some(f) = current {
        figures.push(f);
    }
    if figures.is_empty() {
        return Err("path produced no figures".into());
    }
    Ok(figures)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn p(x: f32, y: f32) -> Point {
        Point { x, y }
    }

    #[test]
    fn parses_a_single_line_figure() {
        let f = parse_path("M1 2L3 4Z").unwrap();
        assert_eq!(f.len(), 1);
        assert_eq!(f[0].start, p(1.0, 2.0));
        assert_eq!(f[0].segments, vec![Segment::Line(p(3.0, 4.0))]);
    }

    #[test]
    fn parses_a_cubic_segment_as_three_points() {
        let f = parse_path("M0 0C1 2 3 4 5 6Z").unwrap();
        assert_eq!(
            f[0].segments,
            vec![Segment::Cubic(p(1.0, 2.0), p(3.0, 4.0), p(5.0, 6.0))]
        );
    }

    #[test]
    fn splits_multiple_subpaths_on_each_moveto() {
        let f = parse_path("M0 0L1 1ZM5 5L6 6Z").unwrap();
        assert_eq!(f.len(), 2);
        assert_eq!(f[1].start, p(5.0, 5.0));
    }

    #[test]
    fn accepts_negative_and_fractional_coordinates() {
        let f = parse_path("M-1.5 2.25L0.5 -3Z").unwrap();
        assert_eq!(f[0].start, p(-1.5, 2.25));
        assert_eq!(f[0].segments, vec![Segment::Line(p(0.5, -3.0))]);
    }

    #[test]
    fn treats_commas_and_extra_whitespace_as_separators() {
        let a = parse_path("M0,0 L1,1 Z").unwrap();
        let b = parse_path("M0 0L1 1Z").unwrap();
        assert_eq!(a[0].segments, b[0].segments);
    }

    #[test]
    fn repeats_the_previous_command_for_bare_coordinate_runs() {
        // "L1 1 2 2" means two lines, per the SVG grammar.
        let f = parse_path("M0 0L1 1 2 2Z").unwrap();
        assert_eq!(f[0].segments.len(), 2);
        assert_eq!(f[0].segments[1], Segment::Line(p(2.0, 2.0)));
    }

    #[test]
    fn rejects_relative_commands_rather_than_misdrawing_them() {
        assert!(parse_path("M0 0l1 1Z").is_err());
    }

    #[test]
    fn v_appends_absolute_vertical_linetos_reusing_the_current_x() {
        let f = parse_path("M1 2V5 8Z").unwrap();
        assert_eq!(
            f[0].segments,
            vec![Segment::Line(p(1.0, 5.0)), Segment::Line(p(1.0, 8.0))]
        );
    }

    #[test]
    fn h_appends_absolute_horizontal_linetos_reusing_the_current_y() {
        let f = parse_path("M1 2H5 8Z").unwrap();
        assert_eq!(
            f[0].segments,
            vec![Segment::Line(p(5.0, 2.0)), Segment::Line(p(8.0, 2.0))]
        );
    }

    #[test]
    fn v_before_any_m_errors() {
        assert!(parse_path("V5Z").is_err());
    }

    #[test]
    fn rejects_lowercase_v_and_h() {
        assert!(parse_path("M0 0v5Z").is_err());
        assert!(parse_path("M0 0h5Z").is_err());
    }

    #[test]
    fn rejects_commands_outside_the_supported_subset() {
        assert!(parse_path("M0 0A1 1 0 0 1 2 2Z").is_err());
    }

    #[test]
    fn rejects_a_truncated_coordinate_run() {
        assert!(parse_path("M0 0L1Z").is_err());
    }

    #[test]
    fn rejects_a_path_that_does_not_begin_with_a_command() {
        assert!(parse_path("1 1Z").is_err());
    }

    #[test]
    fn the_vendored_fluent_glyph_parses_into_three_figures() {
        let figures = parse_path(crate::icon::GLYPH_PATH).unwrap();
        assert_eq!(figures.len(), 3);
        assert!(figures.iter().all(|f| !f.segments.is_empty()));
    }

    #[test]
    fn the_vendored_fluent_glyph_stays_inside_its_view_box() {
        for f in parse_path(crate::icon::GLYPH_PATH).unwrap() {
            let mut pts = vec![f.start];
            for s in &f.segments {
                match s {
                    Segment::Line(a) => pts.push(*a),
                    Segment::Cubic(a, b, c) => pts.extend([*a, *b, *c]),
                }
            }
            for pt in pts {
                assert!(
                    (0.0..=crate::icon::GLYPH_VIEWBOX).contains(&pt.x)
                        && (0.0..=crate::icon::GLYPH_VIEWBOX).contains(&pt.y),
                    "point {pt:?} escapes the view box"
                );
            }
        }
    }

    #[test]
    fn the_vendored_power_glyph_parses_without_error() {
        let figures = parse_path(crate::icon::POWER_PATH).unwrap();
        assert!(!figures.is_empty());
        assert!(figures.iter().all(|f| !f.segments.is_empty()));
    }

    #[test]
    fn the_vendored_power_glyph_stays_inside_its_view_box() {
        for f in parse_path(crate::icon::POWER_PATH).unwrap() {
            let mut pts = vec![f.start];
            for s in &f.segments {
                match s {
                    Segment::Line(a) => pts.push(*a),
                    Segment::Cubic(a, b, c) => pts.extend([*a, *b, *c]),
                }
            }
            for pt in pts {
                assert!(
                    (0.0..=crate::icon::POWER_VIEWBOX).contains(&pt.x)
                        && (0.0..=crate::icon::POWER_VIEWBOX).contains(&pt.y),
                    "point {pt:?} escapes the view box"
                );
            }
        }
    }

    #[test]
    fn the_vendored_checkmark_glyph_parses_without_error() {
        let figures = parse_path(crate::icon::CHECK_PATH).unwrap();
        assert!(!figures.is_empty());
        assert!(figures.iter().all(|f| !f.segments.is_empty()));
    }

    #[test]
    fn the_vendored_checkmark_glyph_stays_inside_its_view_box() {
        for f in parse_path(crate::icon::CHECK_PATH).unwrap() {
            let mut pts = vec![f.start];
            for s in &f.segments {
                match s {
                    Segment::Line(a) => pts.push(*a),
                    Segment::Cubic(a, b, c) => pts.extend([*a, *b, *c]),
                }
            }
            for pt in pts {
                assert!(
                    (0.0..=crate::icon::CHECK_VIEWBOX).contains(&pt.x)
                        && (0.0..=crate::icon::CHECK_VIEWBOX).contains(&pt.y),
                    "point {pt:?} escapes the view box"
                );
            }
        }
    }
}
