//! The only module that talks to the device.
//!
//! Owns a background thread that holds the hidapi handle, runs the connect
//! sequence, and blocks on reads. Never touches UI state directly.

use crate::protocol::{self, Layers};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::thread::{self, JoinHandle};
use std::time::Duration;

/// Discriminants are packed into a window message wParam in main.rs. Do not
/// reorder without updating the unpacking there.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Status {
    Disconnected = 0,
    /// Connected, but all eight expression slots belong to the user, so the
    /// layer cannot be read.
    NoSlot = 1,
    Connected = 2,
    /// Connected, but the firmware reports a config version this app was not
    /// built against. Every opcode and command byte is version-specific, so
    /// nothing is written to the device in this state.
    VersionMismatch = 3,
}

#[derive(Debug, Clone, Copy)]
pub struct State {
    pub status: Status,
    pub layers: Layers,
}

pub struct Handle {
    stop: Arc<AtomicBool>,
    thread: Option<JoinHandle<()>>,
}

impl Handle {
    /// Signals the thread to disable Monitor and exit, then waits for it.
    pub fn shutdown(mut self) {
        self.stop.store(true, Ordering::Relaxed);
        if let Some(t) = self.thread.take() {
            let _ = t.join();
        }
    }
}

const RECONNECT_DELAY: Duration = Duration::from_secs(2);
/// Long enough that an idle device does not spin the CPU, short enough that
/// quit stays responsive.
const READ_TIMEOUT_MS: i32 = 500;

pub fn spawn(on_change: impl Fn(State) + Send + 'static) -> Handle {
    let stop = Arc::new(AtomicBool::new(false));
    let thread_stop = stop.clone();
    let thread = thread::spawn(move || run(thread_stop, on_change));
    Handle { stop, thread: Some(thread) }
}

fn run(stop: Arc<AtomicBool>, on_change: impl Fn(State)) {
    let mut last = State { status: Status::Disconnected, layers: Layers(1) };
    on_change(last);

    while !stop.load(Ordering::Relaxed) {
        if session(&stop, &mut last, &on_change).is_err() && last.status != Status::Disconnected {
            last = State { status: Status::Disconnected, layers: Layers(1) };
            on_change(last);
        }
        if !stop.load(Ordering::Relaxed) {
            thread::sleep(RECONNECT_DELAY);
        }
    }
}

/// One connect-and-read cycle. Returns Err on any device failure so the caller
/// can back off and retry. A fresh HidApi per attempt keeps enumeration
/// results current and avoids shared mutable state.
fn session(
    stop: &AtomicBool,
    last: &mut State,
    on_change: &impl Fn(State),
) -> Result<(), ()> {
    let api = hidapi::HidApi::new().map_err(|_| ())?;

    let info = api
        .device_list()
        .find(|d| {
            d.usage_page() == protocol::CONFIG_USAGE_PAGE && d.usage() == protocol::CONFIG_USAGE
        })
        .ok_or(())?;
    let dev = info.open_device(&api).map_err(|_| ())?;

    // Version gate. Nothing is written to the device until the firmware
    // confirms it speaks the protocol version these opcodes belong to.
    dev.send_feature_report(&protocol::get_config())
        .map_err(|_| ())?;
    let version = read_response(&dev, protocol::parse_config_version)?;
    if version != protocol::CONFIG_VERSION {
        *last = State { status: Status::VersionMismatch, layers: Layers(1) };
        on_change(*last);
        // Hold the handle open so the loop does not spin re-enumerating.
        while !stop.load(Ordering::Relaxed) {
            thread::sleep(RECONNECT_DELAY);
        }
        return Ok(());
    }

    let status = match claim_slot(&dev)? {
        protocol::SlotChoice::NoneFree => Status::NoSlot,
        protocol::SlotChoice::Existing(_) => Status::Connected,
        protocol::SlotChoice::Empty(slot) => {
            dev.send_feature_report(&protocol::append_expression(slot))
                .map_err(|_| ())?;
            // Required. Without RESUME the firmware never marks the expression
            // valid and eval_expr silently returns zero forever.
            dev.send_feature_report(&protocol::resume()).map_err(|_| ())?;
            Status::Connected
        }
    };

    dev.send_feature_report(&protocol::set_monitor_enabled(true))
        .map_err(|_| ())?;

    *last = State { status, layers: Layers(1) };
    on_change(*last);

    let mut buf = [0u8; protocol::MONITOR_REPORT_LEN];
    while !stop.load(Ordering::Relaxed) {
        match dev.read_timeout(&mut buf, READ_TIMEOUT_MS) {
            Ok(0) => continue, // timeout, the device is simply idle
            Ok(_) => {
                if let Some(layers) = protocol::parse_monitor_report(&buf) {
                    if layers != last.layers {
                        last.layers = layers;
                        on_change(*last);
                    }
                }
            }
            Err(_) => return Err(()),
        }
    }

    // Clean shutdown. The expression itself cannot be removed without
    // CLEAR_EXPRESSIONS, which would destroy the user's own expressions, so it
    // is left in RAM where it is inert once Monitor is off.
    let _ = dev.send_feature_report(&protocol::set_monitor_enabled(false));
    Ok(())
}

/// Reads all eight expression slots and decides which to use.
fn claim_slot(dev: &hidapi::HidDevice) -> Result<protocol::SlotChoice, ()> {
    let mut slots = Vec::with_capacity(protocol::NEXPRESSIONS);
    for slot in 0..protocol::NEXPRESSIONS as u8 {
        dev.send_feature_report(&protocol::get_expression(slot))
            .map_err(|_| ())?;
        slots.push(read_response(dev, protocol::parse_expression_response)?);
    }
    Ok(protocol::choose_slot(&slots))
}

/// Reads a config response and runs `parse` over it.
///
/// The firmware answers asynchronously, so a read issued too soon comes back
/// short. Retry with a doubling delay, as the official config tool does.
fn read_response<T>(
    dev: &hidapi::HidDevice,
    parse: impl Fn(&[u8]) -> Option<T>,
) -> Result<T, ()> {
    let mut delay = Duration::from_millis(2);
    for _ in 0..10 {
        let mut buf = [0u8; protocol::PACKET_LEN];
        buf[0] = protocol::REPORT_ID_CONFIG;
        if let Ok(n) = dev.get_feature_report(&mut buf) {
            if n >= protocol::PACKET_LEN {
                if let Some(v) = parse(&buf) {
                    return Ok(v);
                }
            }
        }
        thread::sleep(delay);
        delay *= 2;
    }
    Err(())
}
