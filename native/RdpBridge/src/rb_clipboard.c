/*
 * Clipboard redirection over cliprdr.
 *  Text (CF_UNICODETEXT), both directions:
 *    local -> remote: rb_clipboard_set_text stores the text and announces it;
 *                     the server pulls it with a format data request.
 *    remote -> local: on a server format list with text we request it and hand
 *                     the result to the on_clipboard_text callback.
 *  Files, local -> remote:
 *    rb_clipboard_set_files offers a file list ("FileGroupDescriptorW"); the server
 *    asks for the list and then for sizes / byte ranges, which are read on demand
 *    through the on_file_read callback (only files from the offered list).
 *  Files, remote -> local (on demand):
 *    The server announces "FileGroupDescriptorW"; the app asks for the file list
 *    (rb_clipboard_request_files) and then for byte ranges of each file
 *    (rb_clipboard_request_file_range, FILECONTENTS_RANGE).
 */
#include <windows.h>

#include <stdlib.h>
#include <string.h>

#include <freerdp/freerdp.h>
#include <freerdp/client/cliprdr.h>
#include <freerdp/utils/cliprdr_utils.h>
#include <freerdp/log.h>

#include "rb_internal.h"

#define TAG "com.rdpmonitorswitcher.clipboard"
#define FILE_LIST_FORMAT_NAME "FileGroupDescriptorW"
/* Our id for the registered file list format (registered formats use 0xC000-0xFFFF). */
#define LOCAL_FILE_FORMAT_ID 0xC0DE
/* Upper bound for one FileContents response. */
#define MAX_RANGE_RESPONSE (8u * 1024u * 1024u)

static rb_session* session_of(CliprdrClientContext* cliprdr)
{
	return cliprdr ? (rb_session*)cliprdr->custom : NULL;
}

static UINT send_capabilities(CliprdrClientContext* cliprdr)
{
	CLIPRDR_GENERAL_CAPABILITY_SET general = { 0 };
	general.capabilitySetType = CB_CAPSTYPE_GENERAL;
	general.capabilitySetLength = CB_CAPSTYPE_GENERAL_LEN;
	general.version = CB_CAPS_VERSION_2;
	general.generalFlags = CB_USE_LONG_FORMAT_NAMES | CB_STREAM_FILECLIP_ENABLED |
	                       CB_FILECLIP_NO_FILE_PATHS | CB_HUGE_FILE_SUPPORT_ENABLED;

	CLIPRDR_CAPABILITIES caps = { 0 };
	caps.cCapabilitiesSets = 1;
	caps.capabilitySets = (CLIPRDR_CAPABILITY_SET*)&general;
	return cliprdr->ClientCapabilities(cliprdr, &caps);
}

/* Sends a format data request and remembers the format so the response can be matched. */
static UINT request_format(rb_session* s, CliprdrClientContext* cliprdr, uint32_t format)
{
	EnterCriticalSection(&s->clip_lock);
	if (s->pending_count == ARRAYSIZE(s->pending_formats))
	{
		/* Responses got lost; start over rather than mismatching them. */
		s->pending_count = 0;
	}
	s->pending_formats[s->pending_count++] = format;
	LeaveCriticalSection(&s->clip_lock);

	CLIPRDR_FORMAT_DATA_REQUEST request = { 0 };
	request.common.msgType = CB_FORMAT_DATA_REQUEST;
	request.requestedFormatId = format;
	return cliprdr->ClientFormatDataRequest(cliprdr, &request);
}

static uint32_t pop_pending_format(rb_session* s)
{
	uint32_t format = 0;
	EnterCriticalSection(&s->clip_lock);
	if (s->pending_count > 0)
	{
		format = s->pending_formats[0];
		s->pending_count--;
		memmove(&s->pending_formats[0], &s->pending_formats[1],
		        s->pending_count * sizeof(s->pending_formats[0]));
	}
	LeaveCriticalSection(&s->clip_lock);
	return format;
}

UINT rb_clipboard_announce(rb_session* s)
{
	CliprdrClientContext* cliprdr = s->cliprdr;
	if (!cliprdr || !InterlockedCompareExchange(&s->clip_ready, 0, 0))
		return CHANNEL_RC_OK;

	EnterCriticalSection(&s->clip_lock);
	const BOOL has_files = s->local_file_count > 0;
	const BOOL has_text = !has_files && s->clip_text != NULL;
	LeaveCriticalSection(&s->clip_lock);

	CLIPRDR_FORMAT format = { 0 };
	if (has_files)
	{
		format.formatId = LOCAL_FILE_FORMAT_ID;
		format.formatName = FILE_LIST_FORMAT_NAME;
	}
	else
	{
		format.formatId = CF_UNICODETEXT;
		format.formatName = NULL;
	}

	CLIPRDR_FORMAT_LIST list = { 0 };
	list.common.msgType = CB_FORMAT_LIST;
	list.numFormats = (has_files || has_text) ? 1 : 0;
	list.formats = list.numFormats ? &format : NULL;
	return cliprdr->ClientFormatList(cliprdr, &list);
}

UINT rb_clipboard_send_file_list_request(rb_session* s)
{
	CliprdrClientContext* cliprdr = s->cliprdr;
	EnterCriticalSection(&s->clip_lock);
	const uint32_t format = s->remote_file_format;
	LeaveCriticalSection(&s->clip_lock);

	if (!cliprdr || format == 0)
	{
		if (s->cb.on_file_list)
			s->cb.on_file_list(s->cb.user, 0, NULL, 0);
		return CHANNEL_RC_OK;
	}
	return request_format(s, cliprdr, format);
}

UINT rb_clipboard_send_file_range_request(rb_session* s, uint32_t stream_id, uint32_t list_index,
                                          uint64_t offset, uint32_t length)
{
	CliprdrClientContext* cliprdr = s->cliprdr;
	if (!cliprdr)
	{
		if (s->cb.on_file_data)
			s->cb.on_file_data(s->cb.user, stream_id, 0, NULL, 0);
		return CHANNEL_RC_OK;
	}

	CLIPRDR_FILE_CONTENTS_REQUEST request = { 0 };
	request.common.msgType = CB_FILECONTENTS_REQUEST;
	request.streamId = stream_id;
	request.listIndex = list_index;
	request.dwFlags = FILECONTENTS_RANGE;
	request.nPositionLow = (UINT32)(offset & 0xFFFFFFFF);
	request.nPositionHigh = (UINT32)(offset >> 32);
	request.cbRequested = length;
	request.haveClipDataId = FALSE;
	const UINT rc = cliprdr->ClientFileContentsRequest(cliprdr, &request);
	if (rc != CHANNEL_RC_OK && s->cb.on_file_data)
		s->cb.on_file_data(s->cb.user, stream_id, 0, NULL, 0);
	return rc;
}

static UINT on_monitor_ready(CliprdrClientContext* cliprdr, const CLIPRDR_MONITOR_READY* ready)
{
	rb_session* s = session_of(cliprdr);
	WINPR_UNUSED(ready);
	if (!s)
		return ERROR_INVALID_PARAMETER;

	UINT rc = send_capabilities(cliprdr);
	if (rc != CHANNEL_RC_OK)
		return rc;
	InterlockedExchange(&s->clip_ready, 1);
	rc = rb_clipboard_announce(s);
	if (s->cb.on_event)
		s->cb.on_event(s->cb.user, RB_EVENT_CLIPBOARD_READY, 0, 0);
	return rc;
}

static UINT on_server_capabilities(CliprdrClientContext* cliprdr,
                                   const CLIPRDR_CAPABILITIES* capabilities)
{
	rb_session* s = session_of(cliprdr);
	if (!s)
		return CHANNEL_RC_OK;
	for (UINT32 i = 0; i < capabilities->cCapabilitiesSets; i++)
	{
		const CLIPRDR_CAPABILITY_SET* set = &capabilities->capabilitySets[i];
		if (set->capabilitySetType == CB_CAPSTYPE_GENERAL &&
		    set->capabilitySetLength >= CB_CAPSTYPE_GENERAL_LEN)
		{
			const CLIPRDR_GENERAL_CAPABILITY_SET* general = (const CLIPRDR_GENERAL_CAPABILITY_SET*)set;
			EnterCriticalSection(&s->clip_lock);
			s->server_general_flags = general->generalFlags;
			LeaveCriticalSection(&s->clip_lock);
		}
	}
	return CHANNEL_RC_OK;
}

static UINT on_server_format_list(CliprdrClientContext* cliprdr, const CLIPRDR_FORMAT_LIST* list)
{
	rb_session* s = session_of(cliprdr);
	if (!s)
		return ERROR_INVALID_PARAMETER;

	CLIPRDR_FORMAT_LIST_RESPONSE response = { 0 };
	response.common.msgType = CB_FORMAT_LIST_RESPONSE;
	response.common.msgFlags = CB_RESPONSE_OK;
	UINT rc = cliprdr->ClientFormatListResponse(cliprdr, &response);
	if (rc != CHANNEL_RC_OK)
		return rc;

	BOOL has_text = FALSE;
	uint32_t file_format = 0;
	for (UINT32 i = 0; i < list->numFormats; i++)
	{
		const CLIPRDR_FORMAT* f = &list->formats[i];
		if (f->formatId == CF_UNICODETEXT)
			has_text = TRUE;
		else if (f->formatName && strcmp(f->formatName, FILE_LIST_FORMAT_NAME) == 0)
			file_format = f->formatId;
	}

	EnterCriticalSection(&s->clip_lock);
	s->remote_file_format = file_format;
	const uint32_t sequence = ++s->remote_sequence;
	LeaveCriticalSection(&s->clip_lock);

	if (s->cb.on_event)
		s->cb.on_event(s->cb.user, RB_EVENT_REMOTE_CLIPBOARD,
		               (has_text ? RB_CLIP_TEXT : 0) | (file_format ? RB_CLIP_FILES : 0),
		               (int32_t)sequence);

	/* Text is mirrored automatically; files are only fetched when the user asks for them. */
	if (has_text && !file_format)
		return request_format(s, cliprdr, CF_UNICODETEXT);
	return CHANNEL_RC_OK;
}

static UINT on_server_format_list_response(CliprdrClientContext* cliprdr,
                                           const CLIPRDR_FORMAT_LIST_RESPONSE* response)
{
	rb_session* s = session_of(cliprdr);
	if (s && s->cb.on_event)
		s->cb.on_event(s->cb.user, RB_EVENT_LOCAL_CLIPBOARD_ACCEPTED,
		               (response->common.msgFlags & CB_RESPONSE_OK) ? 1 : 0, 0);
	return CHANNEL_RC_OK;
}

static UINT respond_file_list(rb_session* s, CliprdrClientContext* cliprdr)
{
	CLIPRDR_FORMAT_DATA_RESPONSE response = { 0 };
	response.common.msgType = CB_FORMAT_DATA_RESPONSE;
	response.common.msgFlags = CB_RESPONSE_FAIL;

	BYTE* data = NULL;
	UINT32 length = 0;
	EnterCriticalSection(&s->clip_lock);
	const UINT32 flags =
	    CB_STREAM_FILECLIP_ENABLED | (s->server_general_flags & CB_HUGE_FILE_SUPPORT_ENABLED);
	const UINT rc = s->local_file_count
	                    ? cliprdr_serialize_file_list_ex(flags, s->local_files, s->local_file_count,
	                                                     &data, &length)
	                    : ERROR_NOT_FOUND;
	LeaveCriticalSection(&s->clip_lock);

	if (rc == CHANNEL_RC_OK)
	{
		response.common.msgFlags = CB_RESPONSE_OK;
		response.common.dataLen = length;
		response.requestedFormatData = data;
	}
	else
	{
		WLog_WARN(TAG, "cannot offer local file list: 0x%08" PRIx32, rc);
	}
	const UINT sent = cliprdr->ClientFormatDataResponse(cliprdr, &response);
	free(data);
	return sent;
}

static UINT respond_file_contents(CliprdrClientContext* cliprdr, UINT32 stream_id, BOOL ok,
                                  const BYTE* data, UINT32 length)
{
	CLIPRDR_FILE_CONTENTS_RESPONSE response = { 0 };
	response.common.msgType = CB_FILECONTENTS_RESPONSE;
	response.common.msgFlags = ok ? CB_RESPONSE_OK : CB_RESPONSE_FAIL;
	response.streamId = stream_id;
	response.cbRequested = ok ? length : 0;
	response.requestedData = ok ? data : NULL;
	return cliprdr->ClientFileContentsResponse(cliprdr, &response);
}

static UINT on_server_file_contents_request(CliprdrClientContext* cliprdr,
                                            const CLIPRDR_FILE_CONTENTS_REQUEST* request)
{
	rb_session* s = session_of(cliprdr);
	if (!s)
		return ERROR_INVALID_PARAMETER;

	/* Only entries of the offered list can be read, and only regular files. */
	EnterCriticalSection(&s->clip_lock);
	const BOOL valid = request->listIndex < s->local_file_count &&
	                   (s->local_files[request->listIndex].dwFileAttributes &
	                    FILE_ATTRIBUTE_DIRECTORY) == 0;
	const uint64_t size =
	    valid ? (((uint64_t)s->local_files[request->listIndex].nFileSizeHigh << 32) |
	             s->local_files[request->listIndex].nFileSizeLow)
	          : 0;
	LeaveCriticalSection(&s->clip_lock);

	if (!valid || (request->dwFlags & (FILECONTENTS_SIZE | FILECONTENTS_RANGE)) ==
	                  (FILECONTENTS_SIZE | FILECONTENTS_RANGE))
		return respond_file_contents(cliprdr, request->streamId, FALSE, NULL, 0);

	if (request->dwFlags & FILECONTENTS_SIZE)
	{
		const uint64_t value = size;
		return respond_file_contents(cliprdr, request->streamId, TRUE, (const BYTE*)&value,
		                             sizeof(value));
	}

	if ((request->dwFlags & FILECONTENTS_RANGE) == 0 || !s->cb.on_file_read)
		return respond_file_contents(cliprdr, request->streamId, FALSE, NULL, 0);

	const uint64_t offset = ((uint64_t)request->nPositionHigh << 32) | request->nPositionLow;
	UINT32 wanted = request->cbRequested;
	if (wanted > MAX_RANGE_RESPONSE)
		wanted = MAX_RANGE_RESPONSE;
	if (offset >= size)
		wanted = 0;
	else if (size - offset < wanted)
		wanted = (UINT32)(size - offset);

	BYTE* buffer = wanted ? (BYTE*)malloc(wanted) : NULL;
	if (wanted && !buffer)
		return respond_file_contents(cliprdr, request->streamId, FALSE, NULL, 0);

	uint32_t read = 0;
	const BOOL ok = wanted == 0 || s->cb.on_file_read(s->cb.user, request->listIndex, offset, wanted,
	                                                   buffer, &read) != 0;
	if (read > wanted)
		read = wanted;
	const UINT rc = respond_file_contents(cliprdr, request->streamId, ok, buffer, read);
	free(buffer);
	return rc;
}

static UINT on_server_format_data_request(CliprdrClientContext* cliprdr,
                                          const CLIPRDR_FORMAT_DATA_REQUEST* request)
{
	rb_session* s = session_of(cliprdr);
	CLIPRDR_FORMAT_DATA_RESPONSE response = { 0 };
	response.common.msgType = CB_FORMAT_DATA_RESPONSE;
	response.common.msgFlags = CB_RESPONSE_FAIL;

	if (s && request->requestedFormatId == LOCAL_FILE_FORMAT_ID)
		return respond_file_list(s, cliprdr);
	if (!s || request->requestedFormatId != CF_UNICODETEXT)
		return cliprdr->ClientFormatDataResponse(cliprdr, &response);

	EnterCriticalSection(&s->clip_lock);
	if (s->clip_text)
	{
		response.common.msgFlags = CB_RESPONSE_OK;
		response.common.dataLen = (s->clip_length + 1) * sizeof(uint16_t);
		response.requestedFormatData = (const BYTE*)s->clip_text;
	}
	const UINT rc = cliprdr->ClientFormatDataResponse(cliprdr, &response);
	LeaveCriticalSection(&s->clip_lock);
	return rc;
}

static void deliver_file_list(rb_session* s, const CLIPRDR_FORMAT_DATA_RESPONSE* response)
{
	if (!s->cb.on_file_list)
		return;
	if ((response->common.msgFlags & CB_RESPONSE_OK) == 0 || !response->requestedFormatData)
	{
		s->cb.on_file_list(s->cb.user, 0, NULL, 0);
		return;
	}

	FILEDESCRIPTORW* descriptors = NULL;
	UINT32 count = 0;
	const UINT rc = cliprdr_parse_file_list(response->requestedFormatData,
	                                        response->common.dataLen, &descriptors, &count);
	if (rc != CHANNEL_RC_OK)
	{
		WLog_ERR(TAG, "cannot parse remote file list: 0x%08" PRIx32, rc);
		s->cb.on_file_list(s->cb.user, 0, NULL, 0);
		return;
	}

	rb_file_info* files = count ? (rb_file_info*)calloc(count, sizeof(rb_file_info)) : NULL;
	if (count && !files)
	{
		free(descriptors);
		s->cb.on_file_list(s->cb.user, 0, NULL, 0);
		return;
	}
	for (UINT32 i = 0; i < count; i++)
	{
		FILEDESCRIPTORW* d = &descriptors[i];
		/* cFileName is a fixed 260 WCHAR array; make sure it is terminated. */
		d->cFileName[ARRAYSIZE(d->cFileName) - 1] = 0;
		files[i].name = (const uint16_t*)d->cFileName;
		files[i].size = ((uint64_t)d->nFileSizeHigh << 32) | d->nFileSizeLow;
		files[i].attributes = (d->dwFlags & FD_ATTRIBUTES) ? d->dwFileAttributes : 0;
		files[i].has_size = (d->dwFlags & FD_FILESIZE) ? 1 : 0;
	}
	WLog_INFO(TAG, "remote file list with %" PRIu32 " entries", count);
	s->cb.on_file_list(s->cb.user, 1, files, count);
	free(files);
	free(descriptors);
}

static UINT on_server_format_data_response(CliprdrClientContext* cliprdr,
                                           const CLIPRDR_FORMAT_DATA_RESPONSE* response)
{
	rb_session* s = session_of(cliprdr);
	if (!s)
		return CHANNEL_RC_OK;

	const uint32_t format = pop_pending_format(s);
	if (format == CF_UNICODETEXT)
	{
		if (!s->cb.on_clipboard_text || (response->common.msgFlags & CB_RESPONSE_OK) == 0 ||
		    !response->requestedFormatData)
			return CHANNEL_RC_OK;

		const uint16_t* text = (const uint16_t*)response->requestedFormatData;
		uint32_t length = response->common.dataLen / sizeof(uint16_t);
		while (length > 0 && text[length - 1] == 0)
			length--;
		s->cb.on_clipboard_text(s->cb.user, text, length);
	}
	else if (format != 0)
	{
		deliver_file_list(s, response);
	}
	return CHANNEL_RC_OK;
}

static UINT on_server_file_contents_response(CliprdrClientContext* cliprdr,
                                             const CLIPRDR_FILE_CONTENTS_RESPONSE* response)
{
	rb_session* s = session_of(cliprdr);
	if (!s || !s->cb.on_file_data)
		return CHANNEL_RC_OK;

	const BOOL ok = (response->common.msgFlags & CB_RESPONSE_OK) != 0;
	s->cb.on_file_data(s->cb.user, response->streamId, ok ? 1 : 0,
	                   ok ? response->requestedData : NULL, ok ? response->cbRequested : 0);
	return CHANNEL_RC_OK;
}

void rb_clipboard_init(rb_session* s, CliprdrClientContext* cliprdr)
{
	cliprdr->custom = s;
	cliprdr->MonitorReady = on_monitor_ready;
	cliprdr->ServerCapabilities = on_server_capabilities;
	cliprdr->ServerFormatList = on_server_format_list;
	cliprdr->ServerFormatListResponse = on_server_format_list_response;
	cliprdr->ServerFormatDataRequest = on_server_format_data_request;
	cliprdr->ServerFormatDataResponse = on_server_format_data_response;
	cliprdr->ServerFileContentsResponse = on_server_file_contents_response;
	cliprdr->ServerFileContentsRequest = on_server_file_contents_request;
	s->cliprdr = cliprdr;
}

void rb_clipboard_uninit(rb_session* s, CliprdrClientContext* cliprdr)
{
	InterlockedExchange(&s->clip_ready, 0);
	EnterCriticalSection(&s->clip_lock);
	s->remote_file_format = 0;
	s->pending_count = 0;
	LeaveCriticalSection(&s->clip_lock);
	if (cliprdr)
		cliprdr->custom = NULL;
	s->cliprdr = NULL;
}
