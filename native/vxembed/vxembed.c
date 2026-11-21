/* vde-xembed: the freedesktop system tray protocol (the old X11 one, _NET_SYSTEM_TRAY_Sn + XEmbed) for the session.
 *
 * Programs that still dock their icon through XEmbed (Steam, Wine, older GTK and Qt apps, ...) ask the owner of the
 * _NET_SYSTEM_TRAY_S0 selection to dock a window. This program owns that selection on the session's X server
 * (vwm's Xwayland), keeps each docked icon in a hidden container window, reads its picture back through
 * XComposite, and publishes it on the session bus as a StatusNotifierItem. The shell's tray (and any other
 * StatusNotifierHost) shows it like every other icon; clicks go back to the program as button events.
 *
 * Exit status 2: another tray manager already owns the selection (not worth restarting). */
#define _GNU_SOURCE
#include <errno.h>
#include <poll.h>
#include <signal.h>
#include <stdbool.h>
#include <strings.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>

#include <systemd/sd-bus.h>
#include <xcb/composite.h>
#include <xcb/damage.h>
#include <xcb/xcb.h>

#define ICON_SIZE 24
#define SNI_IFACE "org.kde.StatusNotifierItem"
#define WATCHER_NAME "org.kde.StatusNotifierWatcher"
#define WATCHER_PATH "/StatusNotifierWatcher"

struct icon {
	struct icon *next;
	xcb_window_t win, box;
	xcb_damage_damage_t damage;
	int n;
	char name[96], path[48];
	char id[128], title[256];
	bool shown;            /* _XEMBED_INFO says mapped */
	bool dirty;
	bool registered;
	uint32_t *pixels;      /* ARGB, ICON_SIZE x ICON_SIZE */
	sd_bus_slot *slot;
};

static xcb_connection_t *X;
static xcb_screen_t *screen;
static xcb_window_t manager;
static xcb_atom_t a_selection, a_opcode, a_manager, a_xembed, a_xembed_info, a_orientation, a_net_wm_name, a_utf8, a_wm_name, a_wm_class;
static uint8_t damage_event;
static xcb_visualid_t argb_visual;
static xcb_colormap_t argb_colormap;
static sd_bus *bus;
static struct icon *icons;
static int next_n = 1;
static volatile sig_atomic_t quitting;

static xcb_atom_t atom(const char *name)
{
	xcb_intern_atom_reply_t *r = xcb_intern_atom_reply(X, xcb_intern_atom(X, 0, strlen(name), name), NULL);
	xcb_atom_t a = r ? r->atom : XCB_ATOM_NONE;
	free(r);
	return a;
}

/* ---- the icon's picture ---- */

static void capture(struct icon *ic)
{
	xcb_pixmap_t pm = xcb_generate_id(X);
	xcb_generic_error_t *err = NULL;
	xcb_void_cookie_t named = xcb_composite_name_window_pixmap_checked(X, ic->box, pm);
	if ((err = xcb_request_check(X, named))) {
		if (getenv("VXEMBED_DEBUG")) fprintf(stderr, "name_window_pixmap: error %d\n", err->error_code);
		free(err);
		return;
	}
	xcb_get_image_reply_t *r = xcb_get_image_reply(X,
		xcb_get_image(X, XCB_IMAGE_FORMAT_Z_PIXMAP, pm, 0, 0, ICON_SIZE, ICON_SIZE, ~0u), &err);
	if (err) { if (getenv("VXEMBED_DEBUG")) fprintf(stderr, "get_image: error %d\n", err->error_code); free(err); }
	xcb_free_pixmap(X, pm);
	if (!r) {
		if (getenv("VXEMBED_DEBUG")) fprintf(stderr, "capture: no image\n");
		return;
	}
	int len = xcb_get_image_data_length(r);
	if (len >= ICON_SIZE * ICON_SIZE * 4) {
		const uint32_t *src = (const uint32_t *)xcb_get_image_data(r);
		for (int i = 0; i < ICON_SIZE * ICON_SIZE; i++) {
			uint32_t p = src[i];
			ic->pixels[i] = r->depth == 32 ? p : (p | 0xff000000u);   /* a 24-bit picture has no alpha: opaque */
		}
	}
	if (getenv("VXEMBED_DEBUG")) fprintf(stderr, "capture: depth %d len %d first %08x\n", r->depth, len, ic->pixels[0]);
	free(r);
}

/* ---- properties of the X window ---- */

static void read_text(xcb_window_t w, xcb_atom_t prop, xcb_atom_t type, char *out, size_t cap)
{
	xcb_get_property_reply_t *r = xcb_get_property_reply(X, xcb_get_property(X, 0, w, prop, type, 0, 64), NULL);
	if (r && xcb_get_property_value_length(r) > 0) {
		snprintf(out, cap, "%.*s", xcb_get_property_value_length(r), (char *)xcb_get_property_value(r));
	}
	free(r);
}

static void read_names(struct icon *ic)
{
	ic->id[0] = ic->title[0] = 0;
	xcb_get_property_reply_t *r = xcb_get_property_reply(X, xcb_get_property(X, 0, ic->win, a_wm_class, XCB_ATOM_STRING, 0, 64), NULL);
	if (r && xcb_get_property_value_length(r) > 0) {
		const char *v = xcb_get_property_value(r);
		int len = xcb_get_property_value_length(r);
		const char *second = memchr(v, 0, len);
		snprintf(ic->id, sizeof ic->id, "%s", second && second + 1 < v + len ? second + 1 : v);
	}
	free(r);
	read_text(ic->win, a_net_wm_name, a_utf8, ic->title, sizeof ic->title);
	if (!ic->title[0])
		read_text(ic->win, a_wm_name, XCB_ATOM_STRING, ic->title, sizeof ic->title);
	if (!ic->id[0])
		snprintf(ic->id, sizeof ic->id, "xembed-%u", ic->win);
	if (!ic->title[0])
		snprintf(ic->title, sizeof ic->title, "%s", ic->id);
}

static bool read_mapped(struct icon *ic)
{
	bool mapped = true;   /* no _XEMBED_INFO: assume mapped */
	xcb_get_property_reply_t *r = xcb_get_property_reply(X, xcb_get_property(X, 0, ic->win, a_xembed_info, a_xembed_info, 0, 2), NULL);
	if (r && xcb_get_property_value_length(r) >= 8)
		mapped = ((uint32_t *)xcb_get_property_value(r))[1] & 1;
	free(r);
	return mapped;
}

/* ---- the bus side ---- */

#define STR_PROP(fn, expr) \
	static int fn(sd_bus *b, const char *p, const char *ifc, const char *prop, sd_bus_message *reply, void *ud, sd_bus_error *e) \
	{ struct icon *ic = ud; (void)ic; return sd_bus_message_append(reply, "s", expr); }

STR_PROP(get_category, "ApplicationStatus")
STR_PROP(get_id, ic->id)
STR_PROP(get_title, ic->title)
STR_PROP(get_status, ic->shown ? "Active" : "Passive")
STR_PROP(get_empty, "")
STR_PROP(get_menu, "/NO_DBUSMENU")

static int get_window(sd_bus *b, const char *p, const char *ifc, const char *prop, sd_bus_message *reply, void *ud, sd_bus_error *e)
{
	return sd_bus_message_append(reply, "u", ((struct icon *)ud)->win);
}

static int get_false(sd_bus *b, const char *p, const char *ifc, const char *prop, sd_bus_message *reply, void *ud, sd_bus_error *e)
{
	return sd_bus_message_append(reply, "b", 0);
}

static int get_pixmap(sd_bus *b, const char *p, const char *ifc, const char *prop, sd_bus_message *reply, void *ud, sd_bus_error *e)
{
	struct icon *ic = ud;
	int r = sd_bus_message_open_container(reply, 'a', "(iiay)");
	if (r < 0) return r;
	if (!strcmp(prop, "IconPixmap")) {
		uint8_t bytes[ICON_SIZE * ICON_SIZE * 4];
		for (int i = 0; i < ICON_SIZE * ICON_SIZE; i++) {   /* ARGB32 in network byte order */
			uint32_t px = ic->pixels[i];
			bytes[i * 4] = px >> 24; bytes[i * 4 + 1] = px >> 16; bytes[i * 4 + 2] = px >> 8; bytes[i * 4 + 3] = px;
		}
		r = sd_bus_message_open_container(reply, 'r', "iiay");
		if (r < 0) return r;
		sd_bus_message_append(reply, "ii", ICON_SIZE, ICON_SIZE);
		sd_bus_message_append_array(reply, 'y', bytes, sizeof bytes);
		sd_bus_message_close_container(reply);
	}
	return sd_bus_message_close_container(reply);
}

static int get_tooltip(sd_bus *b, const char *p, const char *ifc, const char *prop, sd_bus_message *reply, void *ud, sd_bus_error *e)
{
	struct icon *ic = ud;
	sd_bus_message_open_container(reply, 'r', "sa(iiay)ss");
	sd_bus_message_append(reply, "s", "");
	sd_bus_message_open_container(reply, 'a', "(iiay)");
	sd_bus_message_close_container(reply);
	sd_bus_message_append(reply, "ss", ic->title, "");
	return sd_bus_message_close_container(reply);
}

/* A click goes to the program as a button press and release on its window, as the old tray managers did. */
static void send_button(struct icon *ic, int button, int rx, int ry)
{
	for (int release = 0; release < 2; release++) {
		xcb_button_press_event_t ev = {0};
		ev.response_type = release ? XCB_BUTTON_RELEASE : XCB_BUTTON_PRESS;
		ev.detail = button;
		ev.time = XCB_CURRENT_TIME;
		ev.root = screen->root;
		ev.event = ic->win;
		ev.child = XCB_NONE;
		ev.root_x = rx; ev.root_y = ry;
		ev.event_x = ICON_SIZE / 2; ev.event_y = ICON_SIZE / 2;
		ev.state = release ? (0x100u << (button - 1)) : 0;
		ev.same_screen = 1;
		xcb_send_event(X, 1, ic->win, release ? XCB_EVENT_MASK_BUTTON_RELEASE : XCB_EVENT_MASK_BUTTON_PRESS, (const char *)&ev);
	}
	xcb_flush(X);
}

static int m_click(sd_bus_message *m, void *ud, sd_bus_error *e, int button)
{
	int x = 0, y = 0;
	sd_bus_message_read(m, "ii", &x, &y);
	send_button(ud, button, x, y);
	return sd_bus_reply_method_return(m, "");
}
static int m_activate(sd_bus_message *m, void *ud, sd_bus_error *e) { return m_click(m, ud, e, 1); }
static int m_secondary(sd_bus_message *m, void *ud, sd_bus_error *e) { return m_click(m, ud, e, 2); }
static int m_context(sd_bus_message *m, void *ud, sd_bus_error *e) { return m_click(m, ud, e, 3); }
static int m_scroll(sd_bus_message *m, void *ud, sd_bus_error *e)
{
	int delta = 0; const char *orient = "";
	sd_bus_message_read(m, "is", &delta, &orient);
	bool vertical = strcasecmp(orient, "horizontal") != 0;
	send_button(ud, vertical ? (delta < 0 ? 4 : 5) : (delta < 0 ? 6 : 7), 0, 0);
	return sd_bus_reply_method_return(m, "");
}

static const sd_bus_vtable item_vtable[] = {
	SD_BUS_VTABLE_START(0),
	SD_BUS_PROPERTY("Category", "s", get_category, 0, SD_BUS_VTABLE_PROPERTY_CONST),
	SD_BUS_PROPERTY("Id", "s", get_id, 0, SD_BUS_VTABLE_PROPERTY_EMITS_CHANGE),
	SD_BUS_PROPERTY("Title", "s", get_title, 0, SD_BUS_VTABLE_PROPERTY_EMITS_CHANGE),
	SD_BUS_PROPERTY("Status", "s", get_status, 0, SD_BUS_VTABLE_PROPERTY_EMITS_CHANGE),
	SD_BUS_PROPERTY("WindowId", "u", get_window, 0, SD_BUS_VTABLE_PROPERTY_CONST),
	SD_BUS_PROPERTY("IconName", "s", get_empty, 0, SD_BUS_VTABLE_PROPERTY_CONST),
	SD_BUS_PROPERTY("IconPixmap", "a(iiay)", get_pixmap, 0, SD_BUS_VTABLE_PROPERTY_EMITS_CHANGE),
	SD_BUS_PROPERTY("OverlayIconName", "s", get_empty, 0, SD_BUS_VTABLE_PROPERTY_CONST),
	SD_BUS_PROPERTY("OverlayIconPixmap", "a(iiay)", get_pixmap, 0, SD_BUS_VTABLE_PROPERTY_CONST),
	SD_BUS_PROPERTY("AttentionIconName", "s", get_empty, 0, SD_BUS_VTABLE_PROPERTY_CONST),
	SD_BUS_PROPERTY("AttentionIconPixmap", "a(iiay)", get_pixmap, 0, SD_BUS_VTABLE_PROPERTY_CONST),
	SD_BUS_PROPERTY("AttentionMovieName", "s", get_empty, 0, SD_BUS_VTABLE_PROPERTY_CONST),
	SD_BUS_PROPERTY("ToolTip", "(sa(iiay)ss)", get_tooltip, 0, SD_BUS_VTABLE_PROPERTY_EMITS_CHANGE),
	SD_BUS_PROPERTY("ItemIsMenu", "b", get_false, 0, SD_BUS_VTABLE_PROPERTY_CONST),
	SD_BUS_PROPERTY("Menu", "o", get_menu, 0, SD_BUS_VTABLE_PROPERTY_CONST),
	SD_BUS_METHOD("Activate", "ii", "", m_activate, SD_BUS_VTABLE_UNPRIVILEGED),
	SD_BUS_METHOD("SecondaryActivate", "ii", "", m_secondary, SD_BUS_VTABLE_UNPRIVILEGED),
	SD_BUS_METHOD("ContextMenu", "ii", "", m_context, SD_BUS_VTABLE_UNPRIVILEGED),
	SD_BUS_METHOD("Scroll", "is", "", m_scroll, SD_BUS_VTABLE_UNPRIVILEGED),
	SD_BUS_SIGNAL("NewTitle", "", 0),
	SD_BUS_SIGNAL("NewIcon", "", 0),
	SD_BUS_SIGNAL("NewStatus", "s", 0),
	SD_BUS_SIGNAL("NewToolTip", "", 0),
	SD_BUS_VTABLE_END
};

static void register_item(struct icon *ic)
{
	char reg[192];
	snprintf(reg, sizeof reg, "%s%s", ic->name, ic->path);   /* "bus-name/object/path" */
	int r = sd_bus_call_method(bus, WATCHER_NAME, WATCHER_PATH, WATCHER_NAME, "RegisterStatusNotifierItem", NULL, NULL, "s", reg);
	ic->registered = r >= 0;
}

static void register_all(void)
{
	for (struct icon *i = icons; i; i = i->next)
		if (!i->registered)
			register_item(i);
}

static bool watcher_present(void)
{
	return sd_bus_get_name_creds(bus, WATCHER_NAME, 0, NULL) >= 0;
}

/* ---- docking ---- */

static void dock(xcb_window_t win)
{
	for (struct icon *i = icons; i; i = i->next)
		if (i->win == win)
			return;
	xcb_get_window_attributes_reply_t *attr = xcb_get_window_attributes_reply(X, xcb_get_window_attributes(X, win), NULL);
	if (!attr)
		return;
	free(attr);

	struct icon *ic = calloc(1, sizeof *ic);
	ic->pixels = calloc(ICON_SIZE * ICON_SIZE, 4);
	ic->win = win;
	ic->n = next_n++;
	snprintf(ic->path, sizeof ic->path, "/StatusNotifierItem/%d", ic->n);
	snprintf(ic->name, sizeof ic->name, "org.kde.StatusNotifierItem-%d-%d", (int)getpid(), ic->n);
	read_names(ic);
	ic->shown = read_mapped(ic);

	/* a hidden container, off the visible area, in the 32-bit visual when there is one so a see-through icon keeps its alpha */
	ic->box = xcb_generate_id(X);
	/* value order follows the XCB_CW_* bits: back pixel, border pixel, override-redirect, event mask, colormap */
	uint32_t vals[] = { 0, 0, 1, XCB_EVENT_MASK_STRUCTURE_NOTIFY | XCB_EVENT_MASK_SUBSTRUCTURE_NOTIFY, argb_colormap };
	uint32_t mask = XCB_CW_BACK_PIXEL | XCB_CW_BORDER_PIXEL | XCB_CW_OVERRIDE_REDIRECT | XCB_CW_EVENT_MASK | (argb_visual ? XCB_CW_COLORMAP : 0);
	xcb_create_window(X, argb_visual ? 32 : XCB_COPY_FROM_PARENT, ic->box, screen->root, -4 * ICON_SIZE, -4 * ICON_SIZE,
		ICON_SIZE, ICON_SIZE, 0, XCB_WINDOW_CLASS_INPUT_OUTPUT, argb_visual ? argb_visual : XCB_COPY_FROM_PARENT, mask, vals);
	xcb_composite_redirect_window(X, ic->box, XCB_COMPOSITE_REDIRECT_MANUAL);

	uint32_t sel = XCB_EVENT_MASK_STRUCTURE_NOTIFY | XCB_EVENT_MASK_PROPERTY_CHANGE;
	xcb_change_window_attributes(X, win, XCB_CW_EVENT_MASK, &sel);
	uint32_t size[] = { ICON_SIZE, ICON_SIZE };
	xcb_configure_window(X, win, XCB_CONFIG_WINDOW_WIDTH | XCB_CONFIG_WINDOW_HEIGHT, size);
	xcb_reparent_window(X, win, ic->box, 0, 0);
	xcb_map_window(X, win);
	xcb_map_window(X, ic->box);

	xcb_client_message_event_t note = {0};
	note.response_type = XCB_CLIENT_MESSAGE; note.format = 32; note.window = win; note.type = a_xembed;
	note.data.data32[0] = XCB_CURRENT_TIME; note.data.data32[1] = 0 /* XEMBED_EMBEDDED_NOTIFY */; note.data.data32[3] = ic->box; note.data.data32[4] = 0;
	xcb_send_event(X, 0, win, XCB_EVENT_MASK_NO_EVENT, (const char *)&note);

	ic->damage = xcb_generate_id(X);
	xcb_damage_create(X, ic->damage, win, XCB_DAMAGE_REPORT_LEVEL_NON_EMPTY);
	xcb_flush(X);
	ic->dirty = true;   /* the first picture follows the first paint */

	ic->next = icons;
	icons = ic;
	sd_bus_add_object_vtable(bus, &ic->slot, ic->path, SNI_IFACE, item_vtable, ic);
	if (sd_bus_request_name(bus, ic->name, 0) < 0)
		fprintf(stderr, "vde-xembed: cannot take %s\n", ic->name);
	if (watcher_present())
		register_item(ic);
}

static void undock(struct icon *ic, bool window_alive)
{
	for (struct icon **p = &icons; *p; p = &(*p)->next)
		if (*p == ic) { *p = ic->next; break; }
	sd_bus_release_name(bus, ic->name);   /* the hosts see the name vanish and drop the icon */
	sd_bus_slot_unref(ic->slot);
	if (window_alive) {
		xcb_reparent_window(X, ic->win, screen->root, 0, 0);
		xcb_unmap_window(X, ic->win);
	}
	xcb_destroy_window(X, ic->box);
	xcb_flush(X);
	free(ic->pixels);
	free(ic);
}

static struct icon *by_window(xcb_window_t w)
{
	for (struct icon *i = icons; i; i = i->next)
		if (i->win == w || i->box == w)
			return i;
	return NULL;
}

static void emit(struct icon *ic, const char *signal)
{
	sd_bus_emit_signal(bus, ic->path, SNI_IFACE, signal, "");
}

static void handle(xcb_generic_event_t *ev)
{
	uint8_t type = ev->response_type & 0x7f;
	if (type == damage_event + XCB_DAMAGE_NOTIFY) {
		xcb_damage_notify_event_t *d = (void *)ev;
		struct icon *ic = by_window(d->drawable);
		if (ic) {
			xcb_damage_subtract(X, ic->damage, XCB_NONE, XCB_NONE);
			ic->dirty = true;
		}
		return;
	}
	switch (type) {
	case XCB_CLIENT_MESSAGE: {
		xcb_client_message_event_t *m = (void *)ev;
		if (m->type == a_opcode && m->data.data32[1] == 0 /* SYSTEM_TRAY_REQUEST_DOCK */)
			dock(m->data.data32[2]);
		break;
	}
	case XCB_SELECTION_CLEAR:
		quitting = 1;   /* someone else took the tray */
		break;
	case XCB_DESTROY_NOTIFY: {
		xcb_destroy_notify_event_t *d = (void *)ev;
		struct icon *ic = by_window(d->window);
		if (ic && ic->win == d->window)
			undock(ic, false);
		break;
	}
	case XCB_REPARENT_NOTIFY: {
		xcb_reparent_notify_event_t *r = (void *)ev;
		struct icon *ic = by_window(r->window);
		if (ic && ic->win == r->window && r->parent != ic->box)
			undock(ic, false);   /* the program took its window back */
		break;
	}
	case XCB_UNMAP_NOTIFY: {
		xcb_unmap_notify_event_t *u = (void *)ev;
		struct icon *ic = by_window(u->window);
		if (ic && ic->win == u->window && ic->shown) {
			ic->shown = false;
			sd_bus_emit_properties_changed(bus, ic->path, SNI_IFACE, "Status", NULL);
		}
		break;
	}
	case XCB_PROPERTY_NOTIFY: {
		xcb_property_notify_event_t *p = (void *)ev;
		struct icon *ic = by_window(p->window);
		if (!ic) break;
		if (p->atom == a_xembed_info) {
			bool mapped = read_mapped(ic);
			if (mapped != ic->shown) {
				ic->shown = mapped;
				if (mapped) xcb_map_window(X, ic->win);
				sd_bus_emit_properties_changed(bus, ic->path, SNI_IFACE, "Status", NULL);
			}
		} else if (p->atom == a_net_wm_name || p->atom == a_wm_name) {
			char before[256];
			snprintf(before, sizeof before, "%s", ic->title);
			ic->title[0] = 0;
			read_text(ic->win, a_net_wm_name, a_utf8, ic->title, sizeof ic->title);
			if (!ic->title[0]) read_text(ic->win, a_wm_name, XCB_ATOM_STRING, ic->title, sizeof ic->title);
			if (!ic->title[0]) snprintf(ic->title, sizeof ic->title, "%s", ic->id);
			if (strcmp(before, ic->title)) emit(ic, "NewTitle");
		}
		break;
	}
	}
}

static void on_signal(int s) { quitting = 1; }

static xcb_connection_t *connect_x(int *scr_num)
{
	for (int tries = 0; tries < 120 && !quitting; tries++) {   /* the compositor's X server may still be starting */
		xcb_connection_t *c = xcb_connect(NULL, scr_num);
		if (!xcb_connection_has_error(c))
			return c;
		xcb_disconnect(c);
		sleep(1);
	}
	return NULL;
}

int main(void)
{
	signal(SIGTERM, on_signal);
	signal(SIGINT, on_signal);
	int scr_num = 0;
	X = connect_x(&scr_num);
	if (!X) {
		fprintf(stderr, "vde-xembed: no X display\n");
		return 1;
	}
	const xcb_setup_t *setup = xcb_get_setup(X);
	xcb_screen_iterator_t it = xcb_setup_roots_iterator(setup);
	for (int i = 0; i < scr_num; i++) xcb_screen_next(&it);
	screen = it.data;

	char selname[32];
	snprintf(selname, sizeof selname, "_NET_SYSTEM_TRAY_S%d", scr_num);
	a_selection = atom(selname);
	a_opcode = atom("_NET_SYSTEM_TRAY_OPCODE");
	a_manager = atom("MANAGER");
	a_xembed = atom("_XEMBED");
	a_xembed_info = atom("_XEMBED_INFO");
	a_orientation = atom("_NET_SYSTEM_TRAY_ORIENTATION");
	a_net_wm_name = atom("_NET_WM_NAME");
	a_utf8 = atom("UTF8_STRING");
	a_wm_name = XCB_ATOM_WM_NAME;
	a_wm_class = XCB_ATOM_WM_CLASS;

	/* the extensions: Composite for the pictures, Damage for knowing when they change */
	xcb_composite_query_version_reply(X, xcb_composite_query_version(X, 0, 4), NULL);
	xcb_damage_query_version_reply(X, xcb_damage_query_version(X, 1, 1), NULL);
	const xcb_query_extension_reply_t *dx = xcb_get_extension_data(X, &xcb_damage_id);
	if (!dx || !dx->present || !xcb_get_extension_data(X, &xcb_composite_id)->present) {
		fprintf(stderr, "vde-xembed: the X server has no Composite/Damage\n");
		return 1;
	}
	damage_event = dx->first_event;

	/* a 32-bit TrueColor visual, for icons with alpha */
	for (xcb_depth_iterator_t d = xcb_screen_allowed_depths_iterator(screen); d.rem; xcb_depth_next(&d))
		if (d.data->depth == 32) {
			xcb_visualtype_t *v = xcb_depth_visuals(d.data);
			if (xcb_depth_visuals_length(d.data) > 0) argb_visual = v->visual_id;
		}
	if (argb_visual) {
		argb_colormap = xcb_generate_id(X);
		xcb_create_colormap(X, XCB_COLORMAP_ALLOC_NONE, argb_colormap, screen->root, argb_visual);
	}

	manager = xcb_generate_id(X);
	uint32_t ovr = 1, mask = XCB_EVENT_MASK_PROPERTY_CHANGE;
	uint32_t vals[] = { ovr, mask };
	xcb_create_window(X, XCB_COPY_FROM_PARENT, manager, screen->root, -1, -1, 1, 1, 0, XCB_WINDOW_CLASS_INPUT_OUTPUT,
		XCB_COPY_FROM_PARENT, XCB_CW_OVERRIDE_REDIRECT | XCB_CW_EVENT_MASK, vals);
	uint32_t horizontal = 0;
	xcb_change_property(X, XCB_PROP_MODE_REPLACE, manager, a_orientation, XCB_ATOM_CARDINAL, 32, 1, &horizontal);

	xcb_get_selection_owner_reply_t *owner = xcb_get_selection_owner_reply(X, xcb_get_selection_owner(X, a_selection), NULL);
	if (owner && owner->owner != XCB_NONE) {
		fprintf(stderr, "vde-xembed: another tray manager owns %s\n", selname);
		free(owner);
		return 2;
	}
	free(owner);
	xcb_set_selection_owner(X, manager, a_selection, XCB_CURRENT_TIME);
	xcb_get_selection_owner_reply_t *check = xcb_get_selection_owner_reply(X, xcb_get_selection_owner(X, a_selection), NULL);
	bool won = check && check->owner == manager;
	free(check);
	if (!won) {
		fprintf(stderr, "vde-xembed: could not take %s\n", selname);
		return 2;
	}
	xcb_client_message_event_t announce = {0};
	announce.response_type = XCB_CLIENT_MESSAGE; announce.format = 32; announce.window = screen->root; announce.type = a_manager;
	announce.data.data32[0] = XCB_CURRENT_TIME; announce.data.data32[1] = a_selection; announce.data.data32[2] = manager;
	xcb_send_event(X, 0, screen->root, XCB_EVENT_MASK_STRUCTURE_NOTIFY, (const char *)&announce);
	xcb_flush(X);

	if (sd_bus_open_user(&bus) < 0) {
		fprintf(stderr, "vde-xembed: no session bus\n");
		return 1;
	}

	struct timespec last_poll = {0}, now;
	bool had_watcher = false;
	while (!quitting) {
		struct pollfd fds[2] = {
			{ .fd = xcb_get_file_descriptor(X), .events = POLLIN },
			{ .fd = sd_bus_get_fd(bus), .events = sd_bus_get_events(bus) },
		};
		poll(fds, 2, 100);
		for (;;) {
			int r = sd_bus_process(bus, NULL);
			if (r <= 0) break;
		}
		xcb_generic_event_t *ev;
		while ((ev = xcb_poll_for_event(X))) {
			handle(ev);
			free(ev);
		}
		if (xcb_connection_has_error(X))
			break;   /* the X server went away */

		clock_gettime(CLOCK_MONOTONIC, &now);
		long ms = (now.tv_sec - last_poll.tv_sec) * 1000 + (now.tv_nsec - last_poll.tv_nsec) / 1000000;
		if (ms >= 50) {   /* pictures are re-read at most 20 times a second */
			last_poll = now;
			for (struct icon *i = icons; i; i = i->next)
				if (i->dirty) {
					i->dirty = false;
					capture(i);
					sd_bus_emit_signal(bus, i->path, SNI_IFACE, "NewIcon", "");
				}
			bool present = watcher_present();
			if (present && !had_watcher) {   /* a (new) watcher: say hello again */
				for (struct icon *i = icons; i; i = i->next) i->registered = false;
				register_all();
			}
			had_watcher = present;
		}
	}
	while (icons)
		undock(icons, true);
	sd_bus_flush_close_unref(bus);
	return 0;
}
