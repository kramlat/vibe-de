/* A stand-in for an XEmbed tray program, for testing vde-xembed: docks a red square and prints the clicks it gets.
 * Not installed.   cc test-client.c -o test-client -lxcb */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <xcb/xcb.h>

static xcb_atom_t atom(xcb_connection_t *c, const char *n)
{
	xcb_intern_atom_reply_t *r = xcb_intern_atom_reply(c, xcb_intern_atom(c, 0, strlen(n), n), 0);
	xcb_atom_t a = r->atom; free(r); return a;
}

int main(void)
{
	int sn; xcb_connection_t *c = xcb_connect(NULL, &sn);
	xcb_screen_t *s = xcb_setup_roots_iterator(xcb_get_setup(c)).data;
	xcb_atom_t sel = atom(c, "_NET_SYSTEM_TRAY_S0"), op = atom(c, "_NET_SYSTEM_TRAY_OPCODE"), info = atom(c, "_XEMBED_INFO");
	xcb_get_selection_owner_reply_t *o = xcb_get_selection_owner_reply(c, xcb_get_selection_owner(c, sel), 0);
	if (!o || !o->owner) { fprintf(stderr, "no tray\n"); return 1; }
	xcb_window_t w = xcb_generate_id(c);
	uint32_t v[] = { 0xff0000, XCB_EVENT_MASK_EXPOSURE | XCB_EVENT_MASK_BUTTON_PRESS | XCB_EVENT_MASK_BUTTON_RELEASE };
	xcb_create_window(c, XCB_COPY_FROM_PARENT, w, s->root, 0, 0, 16, 16, 0, XCB_WINDOW_CLASS_INPUT_OUTPUT, s->root_visual, XCB_CW_BACK_PIXEL | XCB_CW_EVENT_MASK, v);
	xcb_change_property(c, XCB_PROP_MODE_REPLACE, w, XCB_ATOM_WM_NAME, XCB_ATOM_STRING, 8, 9, "test icon");
	xcb_change_property(c, XCB_PROP_MODE_REPLACE, w, XCB_ATOM_WM_CLASS, XCB_ATOM_STRING, 8, 10, "tc\0TestIcon");
	uint32_t i[] = { 0, 1 };
	xcb_change_property(c, XCB_PROP_MODE_REPLACE, w, info, info, 32, 2, i);
	xcb_client_message_event_t m = {0};
	m.response_type = XCB_CLIENT_MESSAGE; m.format = 32; m.window = o->owner; m.type = op;
	m.data.data32[0] = XCB_CURRENT_TIME; m.data.data32[1] = 0; m.data.data32[2] = w;
	xcb_send_event(c, 0, o->owner, XCB_EVENT_MASK_NO_EVENT, (char *)&m);
	xcb_flush(c);
	xcb_generic_event_t *e;
	while ((e = xcb_wait_for_event(c))) {
		int t = e->response_type & 0x7f;
		if (t == XCB_BUTTON_PRESS) printf("press %d\n", ((xcb_button_press_event_t *)e)->detail), fflush(stdout);
		if (t == XCB_BUTTON_RELEASE) printf("release %d\n", ((xcb_button_release_event_t *)e)->detail), fflush(stdout);
		if (t == XCB_EXPOSE) { xcb_clear_area(c, 0, w, 0, 0, 0, 0); xcb_flush(c); }
		free(e);
	}
	return 0;
}
