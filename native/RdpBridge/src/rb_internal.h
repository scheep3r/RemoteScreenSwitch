#ifndef RB_INTERNAL_H
#define RB_INTERNAL_H

#include <freerdp/freerdp.h>
#include <freerdp/client.h>
#include <freerdp/client/disp.h>
#include <freerdp/client/cliprdr.h>
#include <winpr/shell.h>

#include "rdpbridge.h"

typedef enum
{
	RB_CMD_MOUSE,
	RB_CMD_EXT_MOUSE,
	RB_CMD_KEY,
	RB_CMD_SYNC,
	RB_CMD_LAYOUT,
	RB_CMD_CLIPBOARD_ANNOUNCE,
	RB_CMD_REQUEST_FILES,
	RB_CMD_FILE_RANGE
} rb_command_type;

typedef struct rb_command
{
	struct rb_command* next;
	rb_command_type type;
	union
	{
		struct
		{
			uint16_t flags;
			uint16_t x;
			uint16_t y;
		} mouse;
		struct
		{
			BOOL down;
			uint32_t scancode;
		} key;
		uint32_t sync_flags;
		struct
		{
			uint32_t count;
			rb_monitor* monitors;
		} layout;
		struct
		{
			uint32_t stream_id;
			uint32_t list_index;
			uint64_t offset;
			uint32_t length;
		} file_range;
	} u;
} rb_command;

typedef struct
{
	rdpClientContext common;
	rb_session* session;
} rbContext;

struct rb_session
{
	rb_callbacks cb;

	rdpContext* context;
	HANDLE thread;
	volatile LONG started;
	volatile LONG user_disconnect;
	volatile LONG connected;

	/* command queue (any thread -> worker) */
	CRITICAL_SECTION cmd_lock;
	rb_command* cmd_head;
	rb_command* cmd_tail;
	HANDLE cmd_event;

	/* shadow frame buffer (worker -> UI) */
	CRITICAL_SECTION frame_lock;
	uint8_t* frame;
	int32_t frame_width;
	int32_t frame_height;
	int32_t frame_stride;
	int32_t dirty_l, dirty_t, dirty_r, dirty_b; /* r/b exclusive; r == 0 means clean */
	uint32_t generation;
	volatile LONG frame_notified;

	/* display control */
	DispClientContext* disp;
	volatile LONG disp_ready;

	/* clipboard */
	CliprdrClientContext* cliprdr;
	CRITICAL_SECTION clip_lock;
	uint16_t* clip_text; /* local text offered to the server, NUL terminated */
	uint32_t clip_length;
	volatile LONG clip_ready;
	uint32_t remote_file_format; /* format id of "FileGroupDescriptorW", 0 = none */
	uint32_t remote_sequence;    /* incremented on every server format list */
	uint32_t pending_formats[8]; /* FIFO of outstanding format data requests */
	uint32_t pending_count;
	uint32_t server_general_flags;  /* from the server capabilities */
	FILEDESCRIPTORW* local_files;   /* offered to the server; replaces clip_text */
	uint32_t local_file_count;
};

/* rb_pointer.c */
BOOL rb_pointer_register(rdpContext* context);

/* rb_clipboard.c */
void rb_clipboard_init(rb_session* session, CliprdrClientContext* cliprdr);
void rb_clipboard_uninit(rb_session* session, CliprdrClientContext* cliprdr);
UINT rb_clipboard_announce(rb_session* session);
UINT rb_clipboard_send_file_list_request(rb_session* session);
UINT rb_clipboard_send_file_range_request(rb_session* session, uint32_t stream_id,
                                          uint32_t list_index, uint64_t offset, uint32_t length);

#endif
