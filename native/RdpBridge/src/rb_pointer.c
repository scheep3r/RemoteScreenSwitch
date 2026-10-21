/*
 * Remote pointer shapes -> HCURSOR. The receiver gets its own copy of the
 * cursor (CopyIcon) and owns it; the bridge destroys its originals in Free.
 */
#include <windows.h>

#include <freerdp/freerdp.h>
#include <freerdp/graphics.h>
#include <freerdp/gdi/gdi.h>
#include <freerdp/codec/color.h>

#include "rb_internal.h"

typedef struct
{
	rdpPointer pointer;
	HCURSOR cursor;
} rbPointer;

static void notify_cursor(rdpContext* context, int32_t kind, HCURSOR cursor)
{
	rb_session* s = ((rbContext*)context)->session;
	if (s && s->cb.on_cursor)
		s->cb.on_cursor(s->cb.user, kind, cursor);
	else if (cursor)
		DestroyCursor(cursor);
}

static BOOL rb_Pointer_New(rdpContext* context, rdpPointer* pointer)
{
	rbPointer* p = (rbPointer*)pointer;
	const UINT32 width = pointer->width;
	const UINT32 height = pointer->height;
	if (width == 0 || height == 0)
		return TRUE;

	BITMAPV5HEADER bi = { 0 };
	bi.bV5Size = sizeof(bi);
	bi.bV5Width = (LONG)width;
	bi.bV5Height = -(LONG)height; /* top-down */
	bi.bV5Planes = 1;
	bi.bV5BitCount = 32;
	bi.bV5Compression = BI_BITFIELDS;
	bi.bV5RedMask = 0x00FF0000;
	bi.bV5GreenMask = 0x0000FF00;
	bi.bV5BlueMask = 0x000000FF;
	bi.bV5AlphaMask = 0xFF000000;

	void* bits = NULL;
	HDC screen = GetDC(NULL);
	HBITMAP color = CreateDIBSection(screen, (BITMAPINFO*)&bi, DIB_RGB_COLORS, &bits, NULL, 0);
	ReleaseDC(NULL, screen);
	if (!color || !bits)
		return FALSE;

	if (!freerdp_image_copy_from_pointer_data((BYTE*)bits, PIXEL_FORMAT_BGRA32, width * 4, 0, 0,
	                                          width, height, pointer->xorMaskData,
	                                          pointer->lengthXorMask, pointer->andMaskData,
	                                          pointer->lengthAndMask, pointer->xorBpp,
	                                          &context->gdi->palette))
	{
		DeleteObject(color);
		return FALSE;
	}

	HBITMAP mask = CreateBitmap((int)width, (int)height, 1, 1, NULL);
	ICONINFO info = { 0 };
	info.fIcon = FALSE;
	info.xHotspot = pointer->xPos;
	info.yHotspot = pointer->yPos;
	info.hbmColor = color;
	info.hbmMask = mask;
	p->cursor = CreateIconIndirect(&info);
	DeleteObject(color);
	DeleteObject(mask);
	return p->cursor != NULL;
}

static void rb_Pointer_Free(rdpContext* context, rdpPointer* pointer)
{
	rbPointer* p = (rbPointer*)pointer;
	WINPR_UNUSED(context);
	if (p->cursor)
	{
		DestroyCursor(p->cursor);
		p->cursor = NULL;
	}
}

static BOOL rb_Pointer_Set(rdpContext* context, rdpPointer* pointer)
{
	rbPointer* p = (rbPointer*)pointer;
	if (!p->cursor)
	{
		notify_cursor(context, RB_CURSOR_DEFAULT, NULL);
		return TRUE;
	}
	HCURSOR copy = (HCURSOR)CopyIcon(p->cursor);
	notify_cursor(context, copy ? RB_CURSOR_CUSTOM : RB_CURSOR_DEFAULT, copy);
	return TRUE;
}

static BOOL rb_Pointer_SetNull(rdpContext* context)
{
	notify_cursor(context, RB_CURSOR_HIDDEN, NULL);
	return TRUE;
}

static BOOL rb_Pointer_SetDefault(rdpContext* context)
{
	notify_cursor(context, RB_CURSOR_DEFAULT, NULL);
	return TRUE;
}

static BOOL rb_Pointer_SetPosition(rdpContext* context, UINT32 x, UINT32 y)
{
	/* Server-side pointer warps are ignored: the local mouse stays authoritative. */
	WINPR_UNUSED(context);
	WINPR_UNUSED(x);
	WINPR_UNUSED(y);
	return TRUE;
}

BOOL rb_pointer_register(rdpContext* context)
{
	rdpPointer pointer = { 0 };
	pointer.size = sizeof(rbPointer);
	pointer.New = rb_Pointer_New;
	pointer.Free = rb_Pointer_Free;
	pointer.Set = rb_Pointer_Set;
	pointer.SetNull = rb_Pointer_SetNull;
	pointer.SetDefault = rb_Pointer_SetDefault;
	pointer.SetPosition = rb_Pointer_SetPosition;
	graphics_register_pointer(context->graphics, &pointer);
	return TRUE;
}
