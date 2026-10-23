/*
 * Focus channel: a dynamic virtual channel that the helper on the remote PC
 * ("RdpMonitorSwitcher.exe --agent") opens to report which monitor the foreground
 * window is on. FreeRDP loads dynamic channel plugins by name through the addin
 * provider, so we register one that knows this plugin and defers everything else
 * to the built-in table.
 */
#include <windows.h>
#include <string.h>
#include <freerdp/freerdp.h>
#include <freerdp/addin.h>
#include <freerdp/client/channels.h>
#include <freerdp/client/cmdline.h>
#include <freerdp/log.h>
#include <winpr/stream.h>
#include "rb_internal.h"

#define TAG "com.rdpmonitorswitcher.focus"
#define RB_FOCUS_ADDIN_NAME "rmsfocus"

typedef struct
{
	GENERIC_DYNVC_PLUGIN base;
	rb_session* session;
} rb_focus_plugin;

static rb_session* focus_session(IWTSVirtualChannelCallback* callback)
{
	GENERIC_CHANNEL_CALLBACK* c = (GENERIC_CHANNEL_CALLBACK*)callback;
	return c && c->plugin ? ((rb_focus_plugin*)c->plugin)->session : NULL;
}

static void focus_event(rb_session* s, int32_t event, int32_t a, int32_t b)
{
	if (s && s->cb.on_event)
		s->cb.on_event(s->cb.user, event, a, b);
}

static UINT rb_focus_on_data(IWTSVirtualChannelCallback* callback, wStream* data)
{
	rb_session* s = focus_session(callback);
	while (Stream_GetRemainingLength(data) >= 12)
	{
		UINT32 type = 0;
		INT32 x = 0;
		INT32 y = 0;
		Stream_Read_UINT32(data, type);
		Stream_Read_INT32(data, x);
		Stream_Read_INT32(data, y);
		if (type == RB_FOCUS_MSG_FOREGROUND)
			focus_event(s, RB_EVENT_REMOTE_FOREGROUND, x, y);
	}
	return CHANNEL_RC_OK;
}

static UINT rb_focus_on_open(IWTSVirtualChannelCallback* callback)
{
	WLog_INFO(TAG, "remote helper connected");
	focus_event(focus_session(callback), RB_EVENT_FOCUS_AGENT, 1, 0);
	return CHANNEL_RC_OK;
}

static UINT rb_focus_on_close(IWTSVirtualChannelCallback* callback)
{
	WLog_INFO(TAG, "remote helper disconnected");
	focus_event(focus_session(callback), RB_EVENT_FOCUS_AGENT, 0, 0);
	free(callback);
	return CHANNEL_RC_OK;
}

static const IWTSVirtualChannelCallback rb_focus_callbacks = { rb_focus_on_data, rb_focus_on_open,
	                                                           rb_focus_on_close, NULL };

static UINT rb_focus_init(GENERIC_DYNVC_PLUGIN* base, rdpContext* context, rdpSettings* settings)
{
	WINPR_UNUSED(settings);
	((rb_focus_plugin*)base)->session = context ? ((rbContext*)context)->session : NULL;
	return CHANNEL_RC_OK;
}

static UINT VCAPITYPE rb_focus_DVCPluginEntry(IDRDYNVC_ENTRY_POINTS* entry_points)
{
	return freerdp_generic_DVCPluginEntry(entry_points, TAG, RB_FOCUS_CHANNEL_NAME,
	                                      sizeof(rb_focus_plugin), sizeof(GENERIC_CHANNEL_CALLBACK),
	                                      &rb_focus_callbacks, rb_focus_init, NULL);
}

static PVIRTUALCHANNELENTRY rb_addin_provider(LPCSTR name, LPCSTR subsystem, LPCSTR type,
                                              DWORD flags)
{
	if (name && strcmp(name, RB_FOCUS_ADDIN_NAME) == 0 && (flags & FREERDP_ADDIN_CHANNEL_DYNAMIC))
		return WINPR_FUNC_PTR_CAST(rb_focus_DVCPluginEntry, PVIRTUALCHANNELENTRY);
	return freerdp_channels_load_static_addin_entry(name, subsystem, type, flags);
}

BOOL rb_focus_register_provider(void)
{
	return freerdp_register_addin_provider(rb_addin_provider, 0) == 0;
}

BOOL rb_focus_add_channel(rdpSettings* settings)
{
	const char* params[] = { RB_FOCUS_ADDIN_NAME };
	if (freerdp_dynamic_channel_collection_find(settings, RB_FOCUS_ADDIN_NAME))
		return TRUE;
	return freerdp_client_add_dynamic_channel(settings, ARRAYSIZE(params), params);
}
