/*
 * RdpBridge - a small, stable C API over FreeRDP 3 for RdpMonitorSwitcher.
 *
 * Threading model
 *  - Every session owns one worker thread that runs the FreeRDP event loop.
 *  - All rb_send_* / rb_set_* calls are thread-safe: they enqueue a command and
 *    wake the worker, which executes it on the FreeRDP thread.
 *  - Callbacks are invoked on FreeRDP-owned threads (worker or channel threads).
 *    They must return quickly and must not call rb_destroy.
 *  - The frame buffer is a private copy of the remote desktop that is only
 *    written under the frame lock (rb_frame_acquire / rb_frame_release).
 *
 * All strings are UTF-8 unless stated otherwise. Clipboard text is UTF-16LE.
 */
#ifndef RDPBRIDGE_H
#define RDPBRIDGE_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(RDPBRIDGE_EXPORTS)
#define RB_API __declspec(dllexport)
#else
#define RB_API __declspec(dllimport)
#endif
#define RB_CALL __cdecl

#define RB_API_VERSION 4

/* Return codes */
#define RB_OK 0
#define RB_E_INVALID_ARG -1
#define RB_E_INVALID_STATE -2
#define RB_E_NOT_SUPPORTED -3
#define RB_E_OUT_OF_MEMORY -4
#define RB_E_FAILED -5

/* Session states (rb_state_cb) */
#define RB_STATE_DISCONNECTED 0
#define RB_STATE_CONNECTING 1
#define RB_STATE_CONNECTED 2
#define RB_STATE_DISCONNECTING 3

/* Why a session ended (rb_state_cb, RB_STATE_DISCONNECTED only) */
#define RB_REASON_NONE 0          /* still running / not applicable */
#define RB_REASON_USER 1          /* rb_disconnect was called */
#define RB_REASON_NETWORK 2       /* transport / DNS / TLS failure: retrying makes sense */
#define RB_REASON_AUTH 3          /* credentials rejected or missing */
#define RB_REASON_CERTIFICATE 4   /* certificate rejected by the user */
#define RB_REASON_SERVER_ENDED 5  /* logoff, admin disconnect, idle timeout */
#define RB_REASON_REPLACED 6      /* another client took over the session */
#define RB_REASON_OTHER 7

/* Events (rb_event_cb) */
#define RB_EVENT_DESKTOP_RESIZED 1        /* a = width, b = height */
#define RB_EVENT_DISPLAY_CONTROL_READY 2  /* a = max monitors, b = max area factor A */
#define RB_EVENT_CLIPBOARD_READY 3
#define RB_EVENT_REMOTE_CLIPBOARD 4       /* a = RB_CLIP_* flags, b = sequence number */
#define RB_EVENT_LOCAL_CLIPBOARD_ACCEPTED 5 /* server answered our format list: a = 1 ok, 0 failed */
#define RB_EVENT_FOCUS_AGENT 6            /* helper on the remote PC: a = 1 connected, 0 gone */
#define RB_EVENT_REMOTE_FOREGROUND 7      /* a window came to the front: a, b = centre of its monitor
                                             in remote desktop coordinates */

/* Dynamic virtual channel of the remote helper ("RdpMonitorSwitcher.exe --agent").
   Each message: uint32 type, int32 x, int32 y (little endian). */
#define RB_FOCUS_CHANNEL_NAME "RdpMonitorSwitcher.Focus"
#define RB_FOCUS_MSG_FOREGROUND 1

/* Content of the remote clipboard (RB_EVENT_REMOTE_CLIPBOARD) */
#define RB_CLIP_TEXT 0x1
#define RB_CLIP_FILES 0x2

/* Certificate verdicts (rb_certificate_cb return value), same as FreeRDP */
#define RB_CERT_REJECT 0
#define RB_CERT_ACCEPT_PERMANENT 1
#define RB_CERT_ACCEPT_SESSION 2

/* Cursor kinds (rb_cursor_cb) */
#define RB_CURSOR_CUSTOM 0
#define RB_CURSOR_HIDDEN 1
#define RB_CURSOR_DEFAULT 2

/* Mouse flags: identical to the RDP PTR_FLAGS_* / PTR_XFLAGS_* constants */
#define RB_PTR_HWHEEL 0x0400
#define RB_PTR_WHEEL 0x0200
#define RB_PTR_WHEEL_NEGATIVE 0x0100
#define RB_PTR_MOVE 0x0800
#define RB_PTR_DOWN 0x8000
#define RB_PTR_BUTTON1 0x1000
#define RB_PTR_BUTTON2 0x2000
#define RB_PTR_BUTTON3 0x4000
#define RB_PTRX_DOWN 0x8000
#define RB_PTRX_BUTTON1 0x0001
#define RB_PTRX_BUTTON2 0x0002

/* Keyboard toggle states for rb_send_focus_sync */
#define RB_SYNC_SCROLL_LOCK 0x1
#define RB_SYNC_NUM_LOCK 0x2
#define RB_SYNC_CAPS_LOCK 0x4
#define RB_SYNC_KANA_LOCK 0x8

/* Scancode flag for extended keys (same as KBDEXT) */
#define RB_SCANCODE_EXTENDED 0x0100

typedef struct rb_session rb_session;

/* One monitor of the virtual remote desktop. Coordinates are in desktop pixels
 * relative to the primary monitor, which must be at (0, 0). */
typedef struct
{
	int32_t left;
	int32_t top;
	int32_t width;
	int32_t height;
	int32_t is_primary;
	uint32_t physical_width_mm;  /* 0 = unknown */
	uint32_t physical_height_mm; /* 0 = unknown */
} rb_monitor;

typedef struct
{
	uint32_t struct_size; /* sizeof(rb_connect_params) */
	const char* host;
	uint32_t port;
	const char* username;
	const char* domain;   /* may be NULL */
	const char* password; /* may be NULL; never stored or logged by the bridge */
	const char* client_hostname; /* may be NULL */

	uint32_t monitor_count;
	const rb_monitor* monitors;

	uint32_t desktop_scale_factor; /* 100..500 */
	uint32_t device_scale_factor;  /* 100, 140 or 180 */
	uint32_t keyboard_layout;      /* Windows KLID, e.g. 0x00000407; 0 = default */

	int32_t enable_clipboard;
	int32_t enable_h264;
	int32_t enable_audio;
} rb_connect_params;

typedef struct
{
	const uint8_t* data; /* BGRX, 4 bytes per pixel, top-down */
	int32_t width;
	int32_t height;
	int32_t stride;
	/* Region changed since the previous acquire. dirty_width == 0: nothing. */
	int32_t dirty_x;
	int32_t dirty_y;
	int32_t dirty_width;
	int32_t dirty_height;
	/* Incremented on every desktop resize. */
	uint32_t generation;
} rb_frame;

typedef void(RB_CALL* rb_state_cb)(void* user, int32_t state, int32_t reason, uint32_t error_code,
                                   const char* message);
typedef void(RB_CALL* rb_event_cb)(void* user, int32_t event, int32_t a, int32_t b);
/* Called once per batch of frame updates; call rb_frame_acquire to collect them. */
typedef void(RB_CALL* rb_frame_cb)(void* user);
/* old_fingerprint is NULL unless the server certificate changed. Blocks the connect. */
typedef uint32_t(RB_CALL* rb_certificate_cb)(void* user, const char* host, uint32_t port,
                                             const char* common_name, const char* subject,
                                             const char* issuer, const char* fingerprint,
                                             const char* old_fingerprint, uint32_t flags);
/* For RB_CURSOR_CUSTOM, hcursor is an HCURSOR owned by the receiver (DestroyCursor). */
typedef void(RB_CALL* rb_cursor_cb)(void* user, int32_t kind, void* hcursor);
/* Remote clipboard text (UTF-16LE, without terminator). */
typedef void(RB_CALL* rb_clipboard_cb)(void* user, const uint16_t* text, uint32_t length);

/* One entry of a remote file list (FILEDESCRIPTORW). Names are relative paths using '\\'. */
typedef struct
{
	const uint16_t* name; /* UTF-16LE, NUL terminated */
	uint64_t size;
	uint32_t attributes; /* FILE_ATTRIBUTE_* (0x10 = directory) */
	int32_t has_size;
} rb_file_info;

/* Answer to rb_clipboard_request_files. count == 0 with ok == 0: the request failed. */
typedef void(RB_CALL* rb_file_list_cb)(void* user, int32_t ok, const rb_file_info* files,
                                       uint32_t count);
/* Answer to rb_clipboard_request_file_range. Data is only valid during the call. */
typedef void(RB_CALL* rb_file_data_cb)(void* user, uint32_t stream_id, int32_t ok,
                                       const uint8_t* data, uint32_t length);
/* The server reads a local file offered with rb_clipboard_set_files. Called synchronously on a
 * FreeRDP thread: fill `buffer` with up to `length` bytes of file `list_index` from `offset`,
 * store the byte count in *read and return 1 (0 = error). */
typedef int32_t(RB_CALL* rb_file_read_cb)(void* user, uint32_t list_index, uint64_t offset,
                                          uint32_t length, uint8_t* buffer, uint32_t* read);

typedef struct
{
	uint32_t struct_size; /* sizeof(rb_callbacks) */
	void* user;
	rb_state_cb on_state;
	rb_event_cb on_event;
	rb_frame_cb on_frame;
	rb_certificate_cb on_certificate;
	rb_cursor_cb on_cursor;
	rb_clipboard_cb on_clipboard_text;
	rb_file_list_cb on_file_list;  /* API version 2 */
	rb_file_data_cb on_file_data;  /* API version 2 */
	rb_file_read_cb on_file_read;  /* API version 3 */
} rb_callbacks;

RB_API int32_t RB_CALL rb_api_version(void);
/* Configures FreeRDP's WLog output to a file. Call before the first rb_create. */
RB_API int32_t RB_CALL rb_configure_logging(const char* directory, const char* level);

RB_API rb_session* RB_CALL rb_create(const rb_callbacks* callbacks);
/* Starts connecting in the background. Only valid once per session. */
RB_API int32_t RB_CALL rb_connect(rb_session* session, const rb_connect_params* params);
/* Requests a disconnect; returns immediately. */
RB_API void RB_CALL rb_disconnect(rb_session* session);
/* Disconnects, joins the worker thread and frees the session. */
RB_API void RB_CALL rb_destroy(rb_session* session);

/* Locks the frame buffer and returns it with the accumulated dirty region.
 * Returns RB_E_INVALID_STATE (without holding the lock) if no frame exists yet. */
RB_API int32_t RB_CALL rb_frame_acquire(rb_session* session, rb_frame* frame);
RB_API void RB_CALL rb_frame_release(rb_session* session);

/* x/y are desktop coordinates (bounding box of all monitors, top-left = 0/0). */
RB_API int32_t RB_CALL rb_send_mouse(rb_session* session, uint16_t flags, int32_t x, int32_t y);
RB_API int32_t RB_CALL rb_send_extended_mouse(rb_session* session, uint16_t flags, int32_t x,
                                               int32_t y);
/* scancode: low byte = scancode, RB_SCANCODE_EXTENDED for E0 keys. */
RB_API int32_t RB_CALL rb_send_key(rb_session* session, int32_t down, uint32_t scancode);
RB_API int32_t RB_CALL rb_send_focus_sync(rb_session* session, uint32_t toggle_states);

/* Sends a new monitor layout over the Display Control channel.
 * RB_E_NOT_SUPPORTED if the server has not (yet) opened the channel. */
RB_API int32_t RB_CALL rb_set_monitor_layout(rb_session* session, const rb_monitor* monitors,
                                              uint32_t count);

/* Announces local clipboard text to the server (UTF-16LE, no terminator needed). */
RB_API int32_t RB_CALL rb_clipboard_set_text(rb_session* session, const uint16_t* text,
                                              uint32_t length);

/* Offers local files on the clipboard (replaces any offered text). Names are relative paths
 * with '\\'; folders must be listed before their contents. Contents are read on demand
 * through on_file_read. count == 0 withdraws the offer. */
RB_API int32_t RB_CALL rb_clipboard_set_files(rb_session* session, const rb_file_info* files,
                                              uint32_t count);

/* Requests the file list of the remote clipboard (after RB_EVENT_REMOTE_CLIPBOARD with
 * RB_CLIP_FILES). Answered via on_file_list. */
RB_API int32_t RB_CALL rb_clipboard_request_files(rb_session* session);

/* Requests up to `length` bytes of file `list_index` starting at `offset`.
 * Answered via on_file_data with the same stream_id. */
RB_API int32_t RB_CALL rb_clipboard_request_file_range(rb_session* session, uint32_t stream_id,
                                                        uint32_t list_index, uint64_t offset,
                                                        uint32_t length);

#ifdef __cplusplus
}
#endif

#endif /* RDPBRIDGE_H */
