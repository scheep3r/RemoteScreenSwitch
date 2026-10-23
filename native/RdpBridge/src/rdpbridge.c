/*
 * RdpBridge: session lifecycle, FreeRDP event loop, frame buffer, input and
 * display control. See include/rdpbridge.h for the public contract.
 */
#include <winsock2.h>
#include <windows.h>

#include <stdlib.h>
#include <string.h>

#include <freerdp/freerdp.h>
#include <freerdp/client.h>
#include <freerdp/gdi/gdi.h>
#include <freerdp/channels/channels.h>
#include <freerdp/client/channels.h>
#include <freerdp/client/disp.h>
#include <freerdp/client/cliprdr.h>
#include <freerdp/utils/helpers.h>
#include <freerdp/error.h>
#include <freerdp/event.h>
#include <freerdp/log.h>
#include <winpr/wlog.h>

#include "rb_internal.h"

#define TAG "com.rdpmonitorswitcher.bridge"

static INIT_ONCE g_init_once = INIT_ONCE_STATIC_INIT;

static rb_session* session_of(rdpContext* context)
{
	return context ? ((rbContext*)context)->session : NULL;
}

static void notify_state(rb_session* s, int32_t state, int32_t reason, uint32_t error,
                         const char* message)
{
	if (s->cb.on_state)
		s->cb.on_state(s->cb.user, state, reason, error, message ? message : "");
}

static void notify_event(rb_session* s, int32_t event, int32_t a, int32_t b)
{
	if (s->cb.on_event)
		s->cb.on_event(s->cb.user, event, a, b);
}

/* ------------------------------------------------------------------------ */
/* Frame buffer                                                             */
/* ------------------------------------------------------------------------ */

static void mark_dirty_locked(rb_session* s, int32_t l, int32_t t, int32_t r, int32_t b)
{
	if (s->dirty_r == 0)
	{
		s->dirty_l = l;
		s->dirty_t = t;
		s->dirty_r = r;
		s->dirty_b = b;
		return;
	}
	if (l < s->dirty_l)
		s->dirty_l = l;
	if (t < s->dirty_t)
		s->dirty_t = t;
	if (r > s->dirty_r)
		s->dirty_r = r;
	if (b > s->dirty_b)
		s->dirty_b = b;
}

static void notify_frame(rb_session* s)
{
	if (InterlockedCompareExchange(&s->frame_notified, 1, 0) == 0 && s->cb.on_frame)
		s->cb.on_frame(s->cb.user);
}

/* (Re)allocates the shadow buffer to the current GDI size. Caller holds frame_lock. */
static BOOL resize_shadow_locked(rb_session* s, int32_t width, int32_t height)
{
	const int32_t stride = width * 4;
	uint8_t* buffer = (uint8_t*)calloc((size_t)stride * (size_t)height, 1);
	if (!buffer)
		return FALSE;
	free(s->frame);
	s->frame = buffer;
	s->frame_width = width;
	s->frame_height = height;
	s->frame_stride = stride;
	s->generation++;
	s->dirty_l = s->dirty_t = s->dirty_r = s->dirty_b = 0;
	return TRUE;
}

static void copy_rect_locked(rb_session* s, const rdpGdi* gdi, int32_t x, int32_t y, int32_t w,
                             int32_t h)
{
	if (x < 0)
	{
		w += x;
		x = 0;
	}
	if (y < 0)
	{
		h += y;
		y = 0;
	}
	if (x + w > s->frame_width)
		w = s->frame_width - x;
	if (y + h > s->frame_height)
		h = s->frame_height - y;
	if (w <= 0 || h <= 0)
		return;

	const size_t row_bytes = (size_t)w * 4;
	for (int32_t row = 0; row < h; row++)
	{
		const BYTE* src = gdi->primary_buffer + (size_t)(y + row) * gdi->stride + (size_t)x * 4;
		uint8_t* dst = s->frame + (size_t)(y + row) * (size_t)s->frame_stride + (size_t)x * 4;
		memcpy(dst, src, row_bytes);
	}
	mark_dirty_locked(s, x, y, x + w, y + h);
}

static BOOL rb_begin_paint(rdpContext* context)
{
	rdpGdi* gdi = context->gdi;
	if (gdi && gdi->primary && gdi->primary->hdc && gdi->primary->hdc->hwnd)
	{
		HGDI_WND hwnd = gdi->primary->hdc->hwnd;
		hwnd->invalid->null = TRUE;
		hwnd->ninvalid = 0;
	}
	return TRUE;
}

static BOOL rb_end_paint(rdpContext* context)
{
	rb_session* s = session_of(context);
	rdpGdi* gdi = context->gdi;
	if (!s || !gdi || !gdi->primary || !gdi->primary->hdc || !gdi->primary->hdc->hwnd)
		return TRUE;

	HGDI_WND hwnd = gdi->primary->hdc->hwnd;
	if (hwnd->invalid->null || hwnd->ninvalid < 1)
		return TRUE;

	EnterCriticalSection(&s->frame_lock);
	if (s->frame && s->frame_width == gdi->width && s->frame_height == gdi->height)
	{
		for (INT32 i = 0; i < hwnd->ninvalid; i++)
		{
			const GDI_RGN* r = &hwnd->cinvalid[i];
			copy_rect_locked(s, gdi, r->x, r->y, r->w, r->h);
		}
	}
	LeaveCriticalSection(&s->frame_lock);

	notify_frame(s);
	return TRUE;
}

static BOOL rb_desktop_resize(rdpContext* context)
{
	rb_session* s = session_of(context);
	rdpSettings* settings = context->settings;
	const UINT32 width = freerdp_settings_get_uint32(settings, FreeRDP_DesktopWidth);
	const UINT32 height = freerdp_settings_get_uint32(settings, FreeRDP_DesktopHeight);

	EnterCriticalSection(&s->frame_lock);
	BOOL ok = gdi_resize(context->gdi, width, height);
	if (ok)
		ok = resize_shadow_locked(s, (int32_t)width, (int32_t)height);
	if (ok)
		copy_rect_locked(s, context->gdi, 0, 0, (int32_t)width, (int32_t)height);
	LeaveCriticalSection(&s->frame_lock);

	if (!ok)
	{
		WLog_ERR(TAG, "desktop resize to %" PRIu32 "x%" PRIu32 " failed", width, height);
		return FALSE;
	}

	notify_event(s, RB_EVENT_DESKTOP_RESIZED, (int32_t)width, (int32_t)height);
	notify_frame(s);
	return TRUE;
}

/* ------------------------------------------------------------------------ */
/* Display control                                                          */
/* ------------------------------------------------------------------------ */

static UINT rb_disp_caps(DispClientContext* disp, UINT32 maxNumMonitors,
                         UINT32 maxMonitorAreaFactorA, UINT32 maxMonitorAreaFactorB)
{
	rb_session* s = (rb_session*)disp->custom;
	WINPR_UNUSED(maxMonitorAreaFactorB);
	if (!s)
		return CHANNEL_RC_OK;

	WLog_INFO(TAG, "display control ready: max monitors %" PRIu32, maxNumMonitors);
	InterlockedExchange(&s->disp_ready, 1);
	notify_event(s, RB_EVENT_DISPLAY_CONTROL_READY, (int32_t)maxNumMonitors,
	             (int32_t)maxMonitorAreaFactorA);
	return CHANNEL_RC_OK;
}

static void send_layout(rb_session* s, const rb_monitor* monitors, uint32_t count)
{
	if (!s->disp || !s->disp->SendMonitorLayout || count == 0)
		return;

	DISPLAY_CONTROL_MONITOR_LAYOUT* layouts =
	    (DISPLAY_CONTROL_MONITOR_LAYOUT*)calloc(count, sizeof(DISPLAY_CONTROL_MONITOR_LAYOUT));
	if (!layouts)
		return;

	/* MS-RDPEDISP: the primary monitor is at (0,0); others are relative to it (may be negative). */
	int32_t origin_x = monitors[0].left;
	int32_t origin_y = monitors[0].top;
	for (uint32_t i = 0; i < count; i++)
	{
		if (monitors[i].is_primary)
		{
			origin_x = monitors[i].left;
			origin_y = monitors[i].top;
			break;
		}
	}

	rdpSettings* settings = s->context->settings;
	for (uint32_t i = 0; i < count; i++)
	{
		const rb_monitor* m = &monitors[i];
		DISPLAY_CONTROL_MONITOR_LAYOUT* l = &layouts[i];
		l->Flags = m->is_primary ? DISPLAY_CONTROL_MONITOR_PRIMARY : 0;
		l->Left = m->left - origin_x;
		l->Top = m->top - origin_y;
		l->Width = (UINT32)m->width;
		l->Height = (UINT32)m->height;
		l->PhysicalWidth = m->physical_width_mm;
		l->PhysicalHeight = m->physical_height_mm;
		l->Orientation = ORIENTATION_LANDSCAPE;
		l->DesktopScaleFactor = freerdp_settings_get_uint32(settings, FreeRDP_DesktopScaleFactor);
		l->DeviceScaleFactor = freerdp_settings_get_uint32(settings, FreeRDP_DeviceScaleFactor);
	}

	const UINT rc = s->disp->SendMonitorLayout(s->disp, count, layouts);
	if (rc != CHANNEL_RC_OK)
		WLog_ERR(TAG, "SendMonitorLayout failed: 0x%08" PRIx32, rc);
	else
		WLog_INFO(TAG, "monitor layout with %" PRIu32 " monitor(s) sent", count);
	free(layouts);
}

/* ------------------------------------------------------------------------ */
/* Channels                                                                 */
/* ------------------------------------------------------------------------ */

static void rb_on_channel_connected(void* context, const ChannelConnectedEventArgs* e)
{
	rbContext* ctx = (rbContext*)context;
	rb_session* s = ctx->session;

	if (strcmp(e->name, DISP_DVC_CHANNEL_NAME) == 0)
	{
		DispClientContext* disp = (DispClientContext*)e->pInterface;
		disp->custom = s;
		disp->DisplayControlCaps = rb_disp_caps;
		s->disp = disp;
	}
	else if (strcmp(e->name, CLIPRDR_SVC_CHANNEL_NAME) == 0)
	{
		rb_clipboard_init(s, (CliprdrClientContext*)e->pInterface);
	}
	else
	{
		freerdp_client_OnChannelConnectedEventHandler(context, e);
	}
}

static void rb_on_channel_disconnected(void* context, const ChannelDisconnectedEventArgs* e)
{
	rbContext* ctx = (rbContext*)context;
	rb_session* s = ctx->session;

	if (strcmp(e->name, DISP_DVC_CHANNEL_NAME) == 0)
	{
		InterlockedExchange(&s->disp_ready, 0);
		DispClientContext* disp = (DispClientContext*)e->pInterface;
		if (disp)
			disp->custom = NULL;
		s->disp = NULL;
	}
	else if (strcmp(e->name, CLIPRDR_SVC_CHANNEL_NAME) == 0)
	{
		rb_clipboard_uninit(s, (CliprdrClientContext*)e->pInterface);
	}
	else
	{
		freerdp_client_OnChannelDisconnectedEventHandler(context, e);
	}
}

/* ------------------------------------------------------------------------ */
/* Instance callbacks                                                       */
/* ------------------------------------------------------------------------ */

static BOOL rb_pre_connect(freerdp* instance)
{
	rdpContext* context = instance->context;
	if (PubSub_SubscribeChannelConnected(context->pubSub, rb_on_channel_connected) < 0)
		return FALSE;
	if (PubSub_SubscribeChannelDisconnected(context->pubSub, rb_on_channel_disconnected) < 0)
		return FALSE;
	return TRUE;
}

static BOOL rb_post_connect(freerdp* instance)
{
	rdpContext* context = instance->context;
	rb_session* s = session_of(context);

	if (!gdi_init(instance, PIXEL_FORMAT_BGRX32))
		return FALSE;
	if (!rb_pointer_register(context))
		return FALSE;

	context->update->BeginPaint = rb_begin_paint;
	context->update->EndPaint = rb_end_paint;
	context->update->DesktopResize = rb_desktop_resize;

	EnterCriticalSection(&s->frame_lock);
	const BOOL ok = resize_shadow_locked(s, context->gdi->width, context->gdi->height);
	LeaveCriticalSection(&s->frame_lock);
	if (!ok)
		return FALSE;

	notify_event(s, RB_EVENT_DESKTOP_RESIZED, context->gdi->width, context->gdi->height);
	return TRUE;
}

static void rb_post_disconnect(freerdp* instance)
{
	rdpContext* context = instance->context;
	if (!context)
		return;
	PubSub_UnsubscribeChannelConnected(context->pubSub, rb_on_channel_connected);
	PubSub_UnsubscribeChannelDisconnected(context->pubSub, rb_on_channel_disconnected);
	gdi_free(instance);
}

/* Credentials are always supplied up front; never prompt on a console. */
static BOOL rb_authenticate_ex(freerdp* instance, char** username, char** password, char** domain,
                               rdp_auth_reason reason)
{
	WINPR_UNUSED(instance);
	WINPR_UNUSED(domain);
	switch (reason)
	{
		case AUTH_NLA:
		case AUTH_TLS:
		case AUTH_RDP:
		case AUTH_RDSTLS:
			return username && *username && **username && password && *password &&
			       **password;
		default:
			return FALSE;
	}
}

static BOOL rb_choose_smartcard(freerdp* instance, SmartcardCertInfo** cert_list, DWORD count,
                                DWORD* choice, BOOL gateway)
{
	WINPR_UNUSED(instance);
	WINPR_UNUSED(cert_list);
	WINPR_UNUSED(count);
	WINPR_UNUSED(choice);
	WINPR_UNUSED(gateway);
	return FALSE;
}

static DWORD rb_verify_certificate_ex(freerdp* instance, const char* host, UINT16 port,
                                      const char* common_name, const char* subject,
                                      const char* issuer, const char* fingerprint, DWORD flags)
{
	rb_session* s = session_of(instance->context);
	if (!s || !s->cb.on_certificate)
		return RB_CERT_REJECT;
	return s->cb.on_certificate(s->cb.user, host, port, common_name, subject, issuer, fingerprint,
	                            NULL, flags);
}

static DWORD rb_verify_changed_certificate_ex(freerdp* instance, const char* host, UINT16 port,
                                              const char* common_name, const char* subject,
                                              const char* issuer, const char* new_fingerprint,
                                              const char* old_subject, const char* old_issuer,
                                              const char* old_fingerprint, DWORD flags)
{
	rb_session* s = session_of(instance->context);
	WINPR_UNUSED(old_subject);
	WINPR_UNUSED(old_issuer);
	if (!s || !s->cb.on_certificate)
		return RB_CERT_REJECT;
	return s->cb.on_certificate(s->cb.user, host, port, common_name, subject, issuer,
	                            new_fingerprint, old_fingerprint ? old_fingerprint : "",
	                            flags);
}

static BOOL rb_global_init(void)
{
	WSADATA wsa;
	return WSAStartup(MAKEWORD(2, 2), &wsa) == 0;
}

static void rb_global_uninit(void)
{
	WSACleanup();
}

static BOOL rb_client_new(freerdp* instance, rdpContext* context)
{
	WINPR_UNUSED(context);
	instance->PreConnect = rb_pre_connect;
	instance->PostConnect = rb_post_connect;
	instance->PostDisconnect = rb_post_disconnect;
	instance->AuthenticateEx = rb_authenticate_ex;
	instance->ChooseSmartcard = rb_choose_smartcard;
	instance->VerifyCertificateEx = rb_verify_certificate_ex;
	instance->VerifyChangedCertificateEx = rb_verify_changed_certificate_ex;
	return TRUE;
}

static void rb_client_free(freerdp* instance, rdpContext* context)
{
	WINPR_UNUSED(instance);
	WINPR_UNUSED(context);
}

static int rb_client_start(rdpContext* context)
{
	WINPR_UNUSED(context);
	return 0;
}

static int rb_client_stop(rdpContext* context)
{
	WINPR_UNUSED(context);
	return 0;
}

/* ------------------------------------------------------------------------ */
/* Disconnect classification                                                */
/* ------------------------------------------------------------------------ */

static int32_t classify(rb_session* s, UINT32 last_error, UINT32 error_info)
{
	if (InterlockedCompareExchange(&s->user_disconnect, 0, 0))
		return RB_REASON_USER;

	switch (error_info)
	{
		case ERRINFO_DISCONNECTED_BY_OTHER_CONNECTION:
			return RB_REASON_REPLACED;
		case ERRINFO_RPC_INITIATED_DISCONNECT:
		case ERRINFO_RPC_INITIATED_LOGOFF:
		case ERRINFO_IDLE_TIMEOUT:
		case ERRINFO_LOGON_TIMEOUT:
		case ERRINFO_LOGOFF_BY_USER:
		case ERRINFO_RPC_INITIATED_DISCONNECT_BY_USER:
			return RB_REASON_SERVER_ENDED;
		case ERRINFO_SERVER_INSUFFICIENT_PRIVILEGES:
		case ERRINFO_SERVER_FRESH_CREDENTIALS_REQUIRED:
			return RB_REASON_AUTH;
		default:
			break;
	}

	switch (last_error)
	{
		case FREERDP_ERROR_SUCCESS:
			return RB_REASON_NETWORK; /* the connection just went away */
		case FREERDP_ERROR_AUTHENTICATION_FAILED:
		case FREERDP_ERROR_CONNECT_LOGON_FAILURE:
		case FREERDP_ERROR_CONNECT_WRONG_PASSWORD:
		case FREERDP_ERROR_CONNECT_ACCESS_DENIED:
		case FREERDP_ERROR_CONNECT_ACCOUNT_RESTRICTION:
		case FREERDP_ERROR_CONNECT_ACCOUNT_LOCKED_OUT:
		case FREERDP_ERROR_CONNECT_ACCOUNT_DISABLED:
		case FREERDP_ERROR_CONNECT_PASSWORD_EXPIRED:
		case FREERDP_ERROR_CONNECT_PASSWORD_CERTAINLY_EXPIRED:
		case FREERDP_ERROR_CONNECT_PASSWORD_MUST_CHANGE:
		case FREERDP_ERROR_CONNECT_NO_OR_MISSING_CREDENTIALS:
		case FREERDP_ERROR_SERVER_DENIED_CONNECTION:
			return RB_REASON_AUTH;
		case FREERDP_ERROR_CONNECT_CANCELLED:
			/* Not requested via rb_disconnect (checked above): our authentication
			 * callback declined because credentials are missing. */
			return RB_REASON_AUTH;
		case FREERDP_ERROR_DNS_NAME_NOT_FOUND:
		case FREERDP_ERROR_CONNECT_FAILED:
		case FREERDP_ERROR_CONNECT_TRANSPORT_FAILED:
		case FREERDP_ERROR_TLS_CONNECT_FAILED:
			return RB_REASON_NETWORK;
		default:
			break;
	}

	if (last_error == MAKE_FREERDP_ERROR(CONNECT, ERRCONNECT_POST_CONNECT_FAILED) ||
	    last_error == MAKE_FREERDP_ERROR(CONNECT, ERRCONNECT_PRE_CONNECT_FAILED))
		return RB_REASON_OTHER;

	return RB_REASON_NETWORK;
}

/* ------------------------------------------------------------------------ */
/* Command queue                                                            */
/* ------------------------------------------------------------------------ */

static void free_command(rb_command* cmd)
{
	if (!cmd)
		return;
	if (cmd->type == RB_CMD_LAYOUT)
		free(cmd->u.layout.monitors);
	free(cmd);
}

static int32_t enqueue(rb_session* s, rb_command* cmd)
{
	if (!InterlockedCompareExchange(&s->connected, 0, 0))
	{
		free_command(cmd);
		return RB_E_INVALID_STATE;
	}

	EnterCriticalSection(&s->cmd_lock);
	/* Coalesce consecutive pure mouse moves. */
	if (cmd->type == RB_CMD_MOUSE && cmd->u.mouse.flags == PTR_FLAGS_MOVE && s->cmd_tail &&
	    s->cmd_tail->type == RB_CMD_MOUSE && s->cmd_tail->u.mouse.flags == PTR_FLAGS_MOVE)
	{
		s->cmd_tail->u.mouse.x = cmd->u.mouse.x;
		s->cmd_tail->u.mouse.y = cmd->u.mouse.y;
		LeaveCriticalSection(&s->cmd_lock);
		free_command(cmd);
		return RB_OK;
	}
	cmd->next = NULL;
	if (s->cmd_tail)
		s->cmd_tail->next = cmd;
	else
		s->cmd_head = cmd;
	s->cmd_tail = cmd;
	LeaveCriticalSection(&s->cmd_lock);
	SetEvent(s->cmd_event);
	return RB_OK;
}

static void execute(rb_session* s, rb_command* cmd)
{
	rdpInput* input = s->context->input;
	switch (cmd->type)
	{
		case RB_CMD_MOUSE:
			(void)freerdp_input_send_mouse_event(input, cmd->u.mouse.flags, cmd->u.mouse.x,
			                                     cmd->u.mouse.y);
			break;
		case RB_CMD_EXT_MOUSE:
			(void)freerdp_input_send_extended_mouse_event(input, cmd->u.mouse.flags,
			                                              cmd->u.mouse.x, cmd->u.mouse.y);
			break;
		case RB_CMD_KEY:
			(void)freerdp_input_send_keyboard_event_ex(input, cmd->u.key.down, FALSE,
			                                           cmd->u.key.scancode);
			break;
		case RB_CMD_SYNC:
			(void)freerdp_input_send_focus_in_event(input, (UINT16)cmd->u.sync_flags);
			break;
		case RB_CMD_LAYOUT:
			send_layout(s, cmd->u.layout.monitors, cmd->u.layout.count);
			break;
		case RB_CMD_CLIPBOARD_ANNOUNCE:
			(void)rb_clipboard_announce(s);
			break;
		case RB_CMD_REQUEST_FILES:
			(void)rb_clipboard_send_file_list_request(s);
			break;
		case RB_CMD_FILE_RANGE:
			(void)rb_clipboard_send_file_range_request(s, cmd->u.file_range.stream_id,
			                                           cmd->u.file_range.list_index,
			                                           cmd->u.file_range.offset,
			                                           cmd->u.file_range.length);
			break;
	}
}

static void drain_commands(rb_session* s, BOOL run)
{
	EnterCriticalSection(&s->cmd_lock);
	rb_command* cmd = s->cmd_head;
	s->cmd_head = s->cmd_tail = NULL;
	LeaveCriticalSection(&s->cmd_lock);

	while (cmd)
	{
		rb_command* next = cmd->next;
		if (run)
			execute(s, cmd);
		free_command(cmd);
		cmd = next;
	}
}

/* ------------------------------------------------------------------------ */
/* Worker thread                                                            */
/* ------------------------------------------------------------------------ */

static DWORD WINAPI worker(LPVOID arg)
{
	rb_session* s = (rb_session*)arg;
	freerdp* instance = s->context->instance;
	HANDLE handles[MAXIMUM_WAIT_OBJECTS] = { 0 };

	notify_state(s, RB_STATE_CONNECTING, RB_REASON_NONE, 0, NULL);

	if (!freerdp_connect(instance))
	{
		const UINT32 err = freerdp_get_last_error(s->context);
		const int32_t reason = classify(s, err, freerdp_error_info(instance));
		WLog_ERR(TAG, "connect failed: %s [0x%08" PRIx32 "]", freerdp_get_last_error_name(err),
		         err);
		notify_state(s, RB_STATE_DISCONNECTED, reason, err, freerdp_get_last_error_string(err));
		return 0;
	}

	InterlockedExchange(&s->connected, 1);
	notify_state(s, RB_STATE_CONNECTED, RB_REASON_NONE, 0, NULL);

	while (!freerdp_shall_disconnect_context(s->context))
	{
		DWORD count = freerdp_get_event_handles(s->context, handles, ARRAYSIZE(handles) - 1);
		if (count == 0)
		{
			WLog_ERR(TAG, "freerdp_get_event_handles failed");
			break;
		}
		handles[count++] = s->cmd_event;

		const DWORD status = WaitForMultipleObjects(count, handles, FALSE, INFINITE);
		if (status == WAIT_FAILED)
		{
			WLog_ERR(TAG, "WaitForMultipleObjects failed: %" PRIu32, GetLastError());
			break;
		}

		if (!freerdp_check_event_handles(s->context))
		{
			if (freerdp_get_last_error(s->context) == FREERDP_ERROR_SUCCESS)
				WLog_ERR(TAG, "freerdp_check_event_handles failed");
			break;
		}

		drain_commands(s, TRUE);
	}

	InterlockedExchange(&s->connected, 0);
	InterlockedExchange(&s->disp_ready, 0);
	drain_commands(s, FALSE);

	const UINT32 err = freerdp_get_last_error(s->context);
	const UINT32 info = freerdp_error_info(instance);
	const int32_t reason = classify(s, err, info);
	const char* message = (info != ERRINFO_SUCCESS && info != ERRINFO_NONE)
	                          ? freerdp_get_error_info_string(info)
	                          : freerdp_get_last_error_string(err);

	notify_state(s, RB_STATE_DISCONNECTING, RB_REASON_NONE, 0, NULL);
	freerdp_disconnect(instance);
	notify_state(s, RB_STATE_DISCONNECTED, reason, err != FREERDP_ERROR_SUCCESS ? err : info,
	             message);
	return 0;
}

/* ------------------------------------------------------------------------ */
/* Public API                                                               */
/* ------------------------------------------------------------------------ */

static BOOL CALLBACK init_once(PINIT_ONCE once, PVOID param, PVOID* ctx)
{
	WINPR_UNUSED(once);
	WINPR_UNUSED(param);
	WINPR_UNUSED(ctx);
	/* Separate known_hosts / config directory from other FreeRDP clients. */
	(void)freerdp_setApplicationDetails("RdpMonitorSwitcher", "RdpMonitorSwitcher", -1);
	return TRUE;
}

RB_API int32_t RB_CALL rb_api_version(void)
{
	return RB_API_VERSION;
}

RB_API int32_t RB_CALL rb_configure_logging(const char* directory, const char* level)
{
	wLog* root = WLog_GetRoot();
	if (!root)
		return RB_E_FAILED;
	if (level && !WLog_SetStringLogLevel(root, level))
		return RB_E_INVALID_ARG;
	if (directory)
	{
		if (!WLog_SetLogAppenderType(root, WLOG_APPENDER_FILE))
			return RB_E_FAILED;
		wLogAppender* appender = WLog_GetLogAppender(root);
		if (!appender ||
		    !WLog_ConfigureAppender(appender, "outputfilepath", (void*)directory) ||
		    !WLog_ConfigureAppender(appender, "outputfilename", (void*)"freerdp.log"))
			return RB_E_FAILED;
	}
	return RB_OK;
}

RB_API rb_session* RB_CALL rb_create(const rb_callbacks* callbacks)
{
	if (!callbacks || callbacks->struct_size < sizeof(rb_callbacks))
		return NULL;

	InitOnceExecuteOnce(&g_init_once, init_once, NULL, NULL);

	rb_session* s = (rb_session*)calloc(1, sizeof(rb_session));
	if (!s)
		return NULL;
	s->cb = *callbacks;
	InitializeCriticalSection(&s->cmd_lock);
	InitializeCriticalSection(&s->frame_lock);
	InitializeCriticalSection(&s->clip_lock);
	s->cmd_event = CreateEventW(NULL, FALSE, FALSE, NULL);
	if (!s->cmd_event)
		goto fail;

	RDP_CLIENT_ENTRY_POINTS ep = { 0 };
	ep.Version = RDP_CLIENT_INTERFACE_VERSION;
	ep.Size = sizeof(RDP_CLIENT_ENTRY_POINTS_V1);
	ep.GlobalInit = rb_global_init;
	ep.GlobalUninit = rb_global_uninit;
	ep.ContextSize = sizeof(rbContext);
	ep.ClientNew = rb_client_new;
	ep.ClientFree = rb_client_free;
	ep.ClientStart = rb_client_start;
	ep.ClientStop = rb_client_stop;

	s->context = freerdp_client_context_new(&ep);
	if (!s->context)
		goto fail;
	/* after freerdp_client_context_new, which registers the default provider */
	if (!rb_focus_register_provider())
		goto fail;
	((rbContext*)s->context)->session = s;
	return s;

fail:
	if (s->cmd_event)
		CloseHandle(s->cmd_event);
	DeleteCriticalSection(&s->cmd_lock);
	DeleteCriticalSection(&s->frame_lock);
	DeleteCriticalSection(&s->clip_lock);
	free(s);
	return NULL;
}

static BOOL set_string(rdpSettings* settings, FreeRDP_Settings_Keys_String id, const char* value)
{
	return freerdp_settings_set_string(settings, id, (value && *value) ? value : NULL);
}

static BOOL apply_settings(rdpSettings* settings, const rb_connect_params* p)
{
	int32_t min_x = INT32_MAX, min_y = INT32_MAX, max_x = INT32_MIN, max_y = INT32_MIN;
	rdpMonitor* monitors = (rdpMonitor*)calloc(p->monitor_count, sizeof(rdpMonitor));
	if (!monitors)
		return FALSE;

	for (uint32_t i = 0; i < p->monitor_count; i++)
	{
		const rb_monitor* src = &p->monitors[i];
		rdpMonitor* m = &monitors[i];
		m->x = src->left;
		m->y = src->top;
		m->width = src->width;
		m->height = src->height;
		m->is_primary = src->is_primary ? TRUE : FALSE;
		m->orig_screen = i;
		m->attributes.physicalWidth = src->physical_width_mm;
		m->attributes.physicalHeight = src->physical_height_mm;
		m->attributes.orientation = ORIENTATION_LANDSCAPE;
		m->attributes.desktopScaleFactor = p->desktop_scale_factor;
		m->attributes.deviceScaleFactor = p->device_scale_factor;
		if (src->left < min_x)
			min_x = src->left;
		if (src->top < min_y)
			min_y = src->top;
		if (src->left + src->width > max_x)
			max_x = src->left + src->width;
		if (src->top + src->height > max_y)
			max_y = src->top + src->height;
	}

	const BOOL multimon = p->monitor_count > 1;
	BOOL ok = set_string(settings, FreeRDP_ServerHostname, p->host) &&
	          freerdp_settings_set_uint32(settings, FreeRDP_ServerPort, p->port ? p->port : 3389) &&
	          set_string(settings, FreeRDP_Username, p->username) &&
	          set_string(settings, FreeRDP_Domain, p->domain) &&
	          set_string(settings, FreeRDP_Password, p->password) &&
	          (!p->client_hostname ||
	           set_string(settings, FreeRDP_ClientHostname, p->client_hostname)) &&
	          freerdp_settings_set_uint32(settings, FreeRDP_DesktopWidth, (UINT32)(max_x - min_x)) &&
	          freerdp_settings_set_uint32(settings, FreeRDP_DesktopHeight,
	                                      (UINT32)(max_y - min_y)) &&
	          freerdp_settings_set_bool(settings, FreeRDP_UseMultimon, multimon) &&
	          freerdp_settings_set_bool(settings, FreeRDP_SpanMonitors, FALSE) &&
	          freerdp_settings_set_bool(settings, FreeRDP_Fullscreen, FALSE) &&
	          freerdp_settings_set_monitor_def_array_sorted(settings, monitors, p->monitor_count) &&
	          freerdp_settings_set_uint32(settings, FreeRDP_MonitorCount, p->monitor_count) &&
	          freerdp_settings_set_bool(settings, FreeRDP_SupportMonitorLayoutPdu, TRUE) &&
	          freerdp_settings_set_bool(settings, FreeRDP_SupportDisplayControl, TRUE) &&
	          freerdp_settings_set_bool(settings, FreeRDP_DynamicResolutionUpdate, TRUE) &&
	          freerdp_settings_set_uint32(settings, FreeRDP_DesktopScaleFactor,
	                                      p->desktop_scale_factor ? p->desktop_scale_factor : 100) &&
	          freerdp_settings_set_uint32(settings, FreeRDP_DeviceScaleFactor,
	                                      p->device_scale_factor ? p->device_scale_factor : 100) &&
	          freerdp_settings_set_uint32(settings, FreeRDP_ColorDepth, 32) &&
	          freerdp_settings_set_bool(settings, FreeRDP_SoftwareGdi, TRUE) &&
	          freerdp_settings_set_bool(settings, FreeRDP_SupportGraphicsPipeline, TRUE) &&
	          freerdp_settings_set_bool(settings, FreeRDP_GfxH264, p->enable_h264 != 0) &&
	          freerdp_settings_set_bool(settings, FreeRDP_GfxAVC444, p->enable_h264 != 0) &&
	          freerdp_settings_set_bool(settings, FreeRDP_GfxAVC444v2, p->enable_h264 != 0) &&
	          freerdp_settings_set_bool(settings, FreeRDP_RemoteFxCodec, TRUE) &&
	          freerdp_settings_set_bool(settings, FreeRDP_GfxProgressive, TRUE) &&
	          freerdp_settings_set_bool(settings, FreeRDP_NetworkAutoDetect, TRUE) &&
	          freerdp_settings_set_uint32(settings, FreeRDP_ConnectionType,
	                                      CONNECTION_TYPE_AUTODETECT) &&
	          freerdp_settings_set_bool(settings, FreeRDP_SupportHeartbeatPdu, TRUE) &&
	          freerdp_settings_set_bool(settings, FreeRDP_HasExtendedMouseEvent, TRUE) &&
	          freerdp_settings_set_bool(settings, FreeRDP_HasHorizontalWheel, TRUE) &&
	          freerdp_settings_set_bool(settings, FreeRDP_RedirectClipboard,
	                                    p->enable_clipboard != 0) &&
	          freerdp_settings_set_bool(settings, FreeRDP_AudioPlayback, p->enable_audio != 0) &&
	          freerdp_settings_set_bool(settings, FreeRDP_AutoReconnectionEnabled, FALSE) &&
	          freerdp_settings_set_bool(settings, FreeRDP_CertificateCallbackPreferPEM, FALSE);

	if (ok && p->keyboard_layout)
		ok = freerdp_settings_set_uint32(settings, FreeRDP_KeyboardLayout, p->keyboard_layout);
	if (ok)
		ok = rb_focus_add_channel(settings);

	free(monitors);
	return ok;
}

RB_API int32_t RB_CALL rb_connect(rb_session* s, const rb_connect_params* p)
{
	if (!s || !p || p->struct_size < sizeof(rb_connect_params) || !p->host || !*p->host ||
	    p->monitor_count == 0 || p->monitor_count > 16 || !p->monitors)
		return RB_E_INVALID_ARG;
	if (InterlockedCompareExchange(&s->started, 1, 0) != 0)
		return RB_E_INVALID_STATE;

	if (!apply_settings(s->context->settings, p))
	{
		WLog_ERR(TAG, "invalid connection settings");
		return RB_E_INVALID_ARG;
	}
	if (freerdp_client_start(s->context) != 0)
		return RB_E_FAILED;

	s->thread = CreateThread(NULL, 0, worker, s, 0, NULL);
	return s->thread ? RB_OK : RB_E_FAILED;
}

RB_API void RB_CALL rb_disconnect(rb_session* s)
{
	if (!s)
		return;
	InterlockedExchange(&s->user_disconnect, 1);
	(void)freerdp_abort_connect_context(s->context);
	SetEvent(s->cmd_event);
}

RB_API void RB_CALL rb_destroy(rb_session* s)
{
	if (!s)
		return;
	if (s->thread)
	{
		rb_disconnect(s);
		WaitForSingleObject(s->thread, INFINITE);
		CloseHandle(s->thread);
		(void)freerdp_client_stop(s->context);
	}
	freerdp_client_context_free(s->context);
	drain_commands(s, FALSE);
	CloseHandle(s->cmd_event);
	free(s->frame);
	if (s->clip_text)
	{
		SecureZeroMemory(s->clip_text, (s->clip_length + 1) * sizeof(uint16_t));
		free(s->clip_text);
	}
	free(s->local_files);
	DeleteCriticalSection(&s->cmd_lock);
	DeleteCriticalSection(&s->frame_lock);
	DeleteCriticalSection(&s->clip_lock);
	free(s);
}

RB_API int32_t RB_CALL rb_frame_acquire(rb_session* s, rb_frame* f)
{
	if (!s || !f)
		return RB_E_INVALID_ARG;

	EnterCriticalSection(&s->frame_lock);
	InterlockedExchange(&s->frame_notified, 0);
	if (!s->frame)
	{
		LeaveCriticalSection(&s->frame_lock);
		return RB_E_INVALID_STATE;
	}
	f->data = s->frame;
	f->width = s->frame_width;
	f->height = s->frame_height;
	f->stride = s->frame_stride;
	f->generation = s->generation;
	f->dirty_x = s->dirty_l;
	f->dirty_y = s->dirty_t;
	f->dirty_width = s->dirty_r - s->dirty_l;
	f->dirty_height = s->dirty_b - s->dirty_t;
	s->dirty_l = s->dirty_t = s->dirty_r = s->dirty_b = 0;
	return RB_OK; /* lock stays held until rb_frame_release */
}

RB_API void RB_CALL rb_frame_release(rb_session* s)
{
	if (s)
		LeaveCriticalSection(&s->frame_lock);
}

static uint16_t clamp_coord(int32_t v)
{
	if (v < 0)
		return 0;
	if (v > 0xFFFF)
		return 0xFFFF;
	return (uint16_t)v;
}

static rb_command* new_command(rb_command_type type)
{
	rb_command* cmd = (rb_command*)calloc(1, sizeof(rb_command));
	if (cmd)
		cmd->type = type;
	return cmd;
}

RB_API int32_t RB_CALL rb_send_mouse(rb_session* s, uint16_t flags, int32_t x, int32_t y)
{
	if (!s)
		return RB_E_INVALID_ARG;
	rb_command* cmd = new_command(RB_CMD_MOUSE);
	if (!cmd)
		return RB_E_OUT_OF_MEMORY;
	cmd->u.mouse.flags = flags;
	cmd->u.mouse.x = clamp_coord(x);
	cmd->u.mouse.y = clamp_coord(y);
	return enqueue(s, cmd);
}

RB_API int32_t RB_CALL rb_send_extended_mouse(rb_session* s, uint16_t flags, int32_t x, int32_t y)
{
	if (!s)
		return RB_E_INVALID_ARG;
	rb_command* cmd = new_command(RB_CMD_EXT_MOUSE);
	if (!cmd)
		return RB_E_OUT_OF_MEMORY;
	cmd->u.mouse.flags = flags;
	cmd->u.mouse.x = clamp_coord(x);
	cmd->u.mouse.y = clamp_coord(y);
	return enqueue(s, cmd);
}

RB_API int32_t RB_CALL rb_send_key(rb_session* s, int32_t down, uint32_t scancode)
{
	if (!s || (scancode & 0xFF) == 0)
		return RB_E_INVALID_ARG;
	rb_command* cmd = new_command(RB_CMD_KEY);
	if (!cmd)
		return RB_E_OUT_OF_MEMORY;
	cmd->u.key.down = down ? TRUE : FALSE;
	cmd->u.key.scancode = scancode & (0xFF | RB_SCANCODE_EXTENDED);
	return enqueue(s, cmd);
}

RB_API int32_t RB_CALL rb_send_focus_sync(rb_session* s, uint32_t toggle_states)
{
	if (!s)
		return RB_E_INVALID_ARG;
	rb_command* cmd = new_command(RB_CMD_SYNC);
	if (!cmd)
		return RB_E_OUT_OF_MEMORY;
	cmd->u.sync_flags = toggle_states & 0x0F;
	return enqueue(s, cmd);
}

RB_API int32_t RB_CALL rb_set_monitor_layout(rb_session* s, const rb_monitor* monitors,
                                              uint32_t count)
{
	if (!s || !monitors || count == 0 || count > 16)
		return RB_E_INVALID_ARG;
	if (!InterlockedCompareExchange(&s->disp_ready, 0, 0))
		return RB_E_NOT_SUPPORTED;

	for (uint32_t i = 0; i < count; i++)
	{
		const rb_monitor* m = &monitors[i];
		if (m->width < DISPLAY_CONTROL_MIN_MONITOR_WIDTH ||
		    m->width > DISPLAY_CONTROL_MAX_MONITOR_WIDTH || (m->width % 2) != 0 ||
		    m->height < DISPLAY_CONTROL_MIN_MONITOR_HEIGHT ||
		    m->height > DISPLAY_CONTROL_MAX_MONITOR_HEIGHT)
			return RB_E_INVALID_ARG;
	}

	rb_command* cmd = new_command(RB_CMD_LAYOUT);
	if (!cmd)
		return RB_E_OUT_OF_MEMORY;
	cmd->u.layout.monitors = (rb_monitor*)malloc(sizeof(rb_monitor) * count);
	if (!cmd->u.layout.monitors)
	{
		free(cmd);
		return RB_E_OUT_OF_MEMORY;
	}
	memcpy(cmd->u.layout.monitors, monitors, sizeof(rb_monitor) * count);
	cmd->u.layout.count = count;
	return enqueue(s, cmd);
}

RB_API int32_t RB_CALL rb_clipboard_set_text(rb_session* s, const uint16_t* text, uint32_t length)
{
	if (!s || (!text && length))
		return RB_E_INVALID_ARG;

	uint16_t* copy = (uint16_t*)calloc((size_t)length + 1, sizeof(uint16_t));
	if (!copy)
		return RB_E_OUT_OF_MEMORY;
	if (length)
		memcpy(copy, text, (size_t)length * sizeof(uint16_t));

	EnterCriticalSection(&s->clip_lock);
	if (s->clip_text)
	{
		SecureZeroMemory(s->clip_text, (s->clip_length + 1) * sizeof(uint16_t));
		free(s->clip_text);
	}
	s->clip_text = copy;
	s->clip_length = length;
	free(s->local_files); /* text replaces a file offer, like on a real clipboard */
	s->local_files = NULL;
	s->local_file_count = 0;
	LeaveCriticalSection(&s->clip_lock);

	if (!InterlockedCompareExchange(&s->clip_ready, 0, 0))
		return RB_OK; /* announced once the channel is ready */

	rb_command* cmd = new_command(RB_CMD_CLIPBOARD_ANNOUNCE);
	if (!cmd)
		return RB_E_OUT_OF_MEMORY;
	return enqueue(s, cmd);
}

RB_API int32_t RB_CALL rb_clipboard_request_files(rb_session* s)
{
	if (!s)
		return RB_E_INVALID_ARG;
	if (!InterlockedCompareExchange(&s->clip_ready, 0, 0))
		return RB_E_NOT_SUPPORTED;
	EnterCriticalSection(&s->clip_lock);
	const uint32_t format = s->remote_file_format;
	LeaveCriticalSection(&s->clip_lock);
	if (format == 0)
		return RB_E_NOT_SUPPORTED;

	rb_command* cmd = new_command(RB_CMD_REQUEST_FILES);
	if (!cmd)
		return RB_E_OUT_OF_MEMORY;
	return enqueue(s, cmd);
}

RB_API int32_t RB_CALL rb_clipboard_request_file_range(rb_session* s, uint32_t stream_id,
                                                        uint32_t list_index, uint64_t offset,
                                                        uint32_t length)
{
	if (!s || length == 0)
		return RB_E_INVALID_ARG;
	if (!InterlockedCompareExchange(&s->clip_ready, 0, 0))
		return RB_E_NOT_SUPPORTED;

	rb_command* cmd = new_command(RB_CMD_FILE_RANGE);
	if (!cmd)
		return RB_E_OUT_OF_MEMORY;
	cmd->u.file_range.stream_id = stream_id;
	cmd->u.file_range.list_index = list_index;
	cmd->u.file_range.offset = offset;
	cmd->u.file_range.length = length;
	return enqueue(s, cmd);
}

RB_API int32_t RB_CALL rb_clipboard_set_files(rb_session* s, const rb_file_info* files,
                                              uint32_t count)
{
	if (!s || (!files && count))
		return RB_E_INVALID_ARG;

	FILEDESCRIPTORW* descriptors =
	    count ? (FILEDESCRIPTORW*)calloc(count, sizeof(FILEDESCRIPTORW)) : NULL;
	if (count && !descriptors)
		return RB_E_OUT_OF_MEMORY;

	for (uint32_t i = 0; i < count; i++)
	{
		const rb_file_info* f = &files[i];
		FILEDESCRIPTORW* d = &descriptors[i];
		const size_t len = f->name ? wcslen((const wchar_t*)f->name) : 0;
		if (len == 0 || len >= ARRAYSIZE(d->cFileName))
		{
			free(descriptors);
			return RB_E_INVALID_ARG;
		}
		memcpy(d->cFileName, f->name, (len + 1) * sizeof(WCHAR));
		d->dwFlags = FD_ATTRIBUTES | FD_FILESIZE | FD_PROGRESSUI;
		d->dwFileAttributes = f->attributes ? f->attributes : FILE_ATTRIBUTE_NORMAL;
		d->nFileSizeLow = (DWORD)(f->size & 0xFFFFFFFF);
		d->nFileSizeHigh = (DWORD)(f->size >> 32);
	}

	EnterCriticalSection(&s->clip_lock);
	free(s->local_files);
	s->local_files = descriptors;
	s->local_file_count = count;
	if (count && s->clip_text)
	{
		SecureZeroMemory(s->clip_text, (s->clip_length + 1) * sizeof(uint16_t));
		free(s->clip_text);
		s->clip_text = NULL;
		s->clip_length = 0;
	}
	LeaveCriticalSection(&s->clip_lock);

	if (!InterlockedCompareExchange(&s->clip_ready, 0, 0))
		return RB_OK; /* announced once the channel is ready */

	rb_command* cmd = new_command(RB_CMD_CLIPBOARD_ANNOUNCE);
	if (!cmd)
		return RB_E_OUT_OF_MEMORY;
	return enqueue(s, cmd);
}
