/* vwm: the Vibe Window Manager, VDE's compositor.
 *
 * Built on wlroots. Speaks Wayland to its clients (xdg-shell, layer-shell for panels, foreign-toplevel so any
 * taskbar can list windows) and runs X11 clients through XWayland. Where it runs follows wlroots' backend choice:
 * on a bare seat it drives the screens itself (DRM/libinput); inside a Wayland or X11 session it opens a window,
 * which is how it is developed and the X11 fallback when there is no DRM.
 *
 * Policy that the shell owns (the window list, activating, minimizing...) goes over the socket in ipc.c.
 * Everything here is deliberately small; titlebars and snapping come next, see docs/vwm.md. */
#define _POSIX_C_SOURCE 200809L
#include "vwm.h"

#include <assert.h>
#include <getopt.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <sys/wait.h>
#include <unistd.h>

#include <wayland-server-core.h>
#include <drm_fourcc.h>
#include <wlr/backend.h>
#include <wlr/interfaces/wlr_buffer.h>
#include <wlr/backend/session.h>
#include <wlr/render/allocator.h>
#include <wlr/render/wlr_renderer.h>
#include <wlr/types/wlr_compositor.h>
#include <wlr/types/wlr_cursor.h>
#include <wlr/types/wlr_data_device.h>
#include <wlr/types/wlr_fractional_scale_v1.h>
#include <wlr/types/wlr_foreign_toplevel_management_v1.h>
#include <wlr/types/wlr_input_device.h>
#include <wlr/types/wlr_keyboard.h>
#include <wlr/types/wlr_keyboard_group.h>
#include <wlr/types/wlr_layer_shell_v1.h>
#include <wlr/types/wlr_output.h>
#include <wlr/types/wlr_output_layout.h>
#include <wlr/types/wlr_pointer.h>
#include <wlr/types/wlr_primary_selection.h>
#include <wlr/types/wlr_primary_selection_v1.h>
#include <wlr/types/wlr_scene.h>
#include <wlr/types/wlr_seat.h>
#include <wlr/types/wlr_subcompositor.h>
#include <wlr/types/wlr_viewporter.h>
#include <wlr/types/wlr_xcursor_manager.h>
#include <wlr/types/wlr_xdg_decoration_v1.h>
#include <wlr/types/wlr_xdg_output_v1.h>
#include <wlr/types/wlr_xdg_shell.h>
#include <wlr/util/log.h>
#include <wlr/xwayland/xwayland.h>
#include <xkbcommon/xkbcommon.h>

#define DECORATION_MODE(s) ((s)->no_chrome ? WLR_XDG_TOPLEVEL_DECORATION_V1_MODE_CLIENT_SIDE : WLR_XDG_TOPLEVEL_DECORATION_V1_MODE_SERVER_SIDE)

enum cursor_mode { CURSOR_PASSTHROUGH, CURSOR_MOVE, CURSOR_RESIZE };

enum chrome_kind { HIT_TITLE = 1, HIT_GRIP, HIT_MENU };
enum chrome_button { BTN_NONE, BTN_MIN, BTN_MAX, BTN_CLOSE };

/* What a titlebar or grip node knows about itself (node->data of its buffer). */
struct chrome_hit {
	struct vwm_view *view;
	enum chrome_kind kind;
	uint32_t edges;   /* grips */
};

enum layer_index { LAYER_BACKGROUND, LAYER_BOTTOM, LAYER_WINDOWS, LAYER_TOP, LAYER_OVERLAY, LAYER_COUNT };

struct vwm_output {
	struct wl_list link;
	struct vwm_server *server;
	struct wlr_output *wlr_output;
	struct wlr_scene_output *scene_output;
	struct wlr_box usable;   /* what is left after panels reserved their edges */
	struct wl_listener frame, request_state, destroy;
};

struct vwm_view {
	struct wl_list link;
	struct vwm_server *server;
	uint32_t id;
	struct wlr_xdg_toplevel *xdg;
	struct wlr_xwayland_surface *x11;
	struct wlr_scene_tree *tree;
	struct wlr_scene_tree *surface_tree;
	struct wlr_foreign_toplevel_handle_v1 *handle;
	bool mapped, minimized, maximized, fullscreen;
	struct wlr_box restore;   /* geometry before maximizing */
	int x, y;
	struct wl_listener map, unmap, commit, destroy, request_move, request_resize, request_maximize,
		request_fullscreen, request_minimize, set_title, set_app_id;
	/* X11 */
	struct wl_listener associate, dissociate, request_configure, request_activate, request_close,
		x11_set_title, x11_set_class;
	/* server-side chrome: the titlebar image comes from the shell, vwm positions it and handles the pointer */
	bool decorated, chrome_pending, chrome_dirty, pressed;
	enum chrome_button hover;
	struct wlr_scene_tree *chrome;
	struct wlr_scene_buffer *title_image, *grips[5];
	struct wlr_scene_rect *title_fallback;
	struct chrome_hit title_hit, grip_hit[5];
	int last_w, last_h;
	struct wlr_xdg_toplevel_decoration_v1 *decoration;
	struct wl_listener decoration_destroy, decoration_mode;
	struct wl_listener x11_commit;
	bool unmanaged;   /* X11 menus and tooltips: shown, but not windows the shell lists */
	struct wl_listener handle_activate, handle_close, handle_minimize, handle_maximize;
};

struct vwm_layer {
	struct wl_list link;
	struct vwm_server *server;
	struct wlr_layer_surface_v1 *surface;
	struct wlr_scene_layer_surface_v1 *scene;
	struct wl_listener map, unmap, commit, destroy;
};

struct vwm_keyboard {
	struct wl_list link;
	struct vwm_server *server;
	struct wlr_keyboard *keyboard;
	struct wl_listener modifiers, key, destroy;
};

struct vwm_server {
	struct wl_display *display;
	struct wlr_backend *backend;
	struct wlr_session *session;   // NULL when running nested
	struct wlr_renderer *renderer;
	struct wlr_allocator *allocator;
	struct wlr_scene *scene;
	struct wlr_scene_output_layout *scene_layout;
	struct wlr_scene_tree *layers[LAYER_COUNT];
	struct wlr_scene_rect *background;
	struct wlr_compositor *compositor;
	struct wlr_output_layout *output_layout;
	struct wl_list outputs;
	struct wlr_xdg_shell *xdg_shell;
	struct wlr_layer_shell_v1 *layer_shell;
	struct wlr_foreign_toplevel_manager_v1 *toplevels;
	struct wlr_xwayland *xwayland;
	struct wlr_cursor *cursor;
	struct wlr_xcursor_manager *xcursor;
	struct wlr_seat *seat;
	struct wlr_keyboard_group *virtual_keyboard;   /* holds a keymap for the seat when no real keyboard is attached */
	struct wl_list keyboards, views, layer_surfaces;   /* views: most recently used first */
	uint32_t next_id;
	struct vwm_view *focused;
	struct vwm_ipc *ipc;
	const char *launcher;
	bool no_chrome;

	enum cursor_mode mode;
	struct vwm_view *grabbed;
	struct vwm_view *hover_view, *pressed_view;
	enum chrome_button pressed_button;
	/* the window menu */
	struct vwm_view *menu_view;
	struct wlr_scene_tree *menu_tree;
	struct wlr_scene_rect *menu_fallback;
	struct wlr_scene_buffer *menu_image;
	struct chrome_hit menu_hit;
	int menu_hover;
	bool menu_pending, menu_dirty, sticky_grab;
	struct vwm_view *last_click_view;
	uint32_t last_click_time;
	bool server_decorations;
	enum { DBL_MAXIMIZE, DBL_MINIMIZE, DBL_CLOSE, DBL_NONE } doubleclick;
	double grab_x, grab_y;
	struct wlr_box grab_box;
	uint32_t resize_edges;

	struct wl_listener new_output, new_xdg_toplevel, new_layer, new_x11, x11_ready, new_input,
		cursor_motion, cursor_motion_absolute, cursor_button, cursor_axis, cursor_frame,
		request_cursor, request_set_selection, request_set_primary, new_decoration;
};

static struct vwm_server *the_server;

/* ---------- small helpers ---------- */

#define FROM(type, ptr, member) (wl_container_of(ptr, (type *)NULL, member))

struct wl_event_loop *vwm_event_loop(struct vwm_server *s) { return wl_display_get_event_loop(s->display); }
void vwm_quit(struct vwm_server *s) { wl_display_terminate(s->display); }

void vwm_set_titlebar_doubleclick(struct vwm_server *s, const char *action)
{
	if (!strcmp(action, "maximize")) s->doubleclick = DBL_MAXIMIZE;
	else if (!strcmp(action, "minimize")) s->doubleclick = DBL_MINIMIZE;
	else if (!strcmp(action, "close")) s->doubleclick = DBL_CLOSE;
	else if (!strcmp(action, "none")) s->doubleclick = DBL_NONE;
}

static struct vwm_output *primary_output(struct vwm_server *s)
{
	struct vwm_output *o;
	wl_list_for_each(o, &s->outputs, link)
		return o;
	return NULL;
}

static struct wlr_box usable_area(struct vwm_server *s)
{
	struct vwm_output *o = primary_output(s);
	if (o)
		return o->usable;
	return (struct wlr_box){ 0, 0, 1280, 720 };
}

static void spawn(const char *command)
{
	if (!command || !*command)
		return;
	/* Double fork: the program is reparented to init, so nothing here has to reap it (and SIGCHLD stays at its
	 * default, which XWayland's own children rely on). */
	pid_t pid = fork();
	if (pid == 0) {
		if (fork() == 0) {
			setsid();
			execl("/bin/sh", "sh", "-c", command, (char *)NULL);
			_exit(127);
		}
		_exit(0);
	}
	if (pid > 0)
		waitpid(pid, NULL, 0);
}

/* ---------- pixel buffers (titlebar images, transparent hit areas) ---------- */

struct pixbuf {
	struct wlr_buffer base;
	void *data;
	size_t stride;
};

static void pixbuf_destroy(struct wlr_buffer *b)
{
	struct pixbuf *p = wl_container_of(b, p, base);
	free(p->data);
	free(p);
}

static bool pixbuf_begin(struct wlr_buffer *b, uint32_t flags, void **data, uint32_t *format, size_t *stride)
{
	struct pixbuf *p = wl_container_of(b, p, base);
	*data = p->data;
	*format = DRM_FORMAT_ARGB8888;
	*stride = p->stride;
	return true;
}

static void pixbuf_end(struct wlr_buffer *b) {}

static const struct wlr_buffer_impl pixbuf_impl = {
	.destroy = pixbuf_destroy,
	.begin_data_ptr_access = pixbuf_begin,
	.end_data_ptr_access = pixbuf_end,
};

/* Takes `data` (w*h*4 bytes of premultiplied ARGB8888); NULL gives a transparent buffer. */
static struct pixbuf *pixbuf_create(int w, int h, void *data)
{
	struct pixbuf *p = calloc(1, sizeof *p);
	p->data = data ? data : calloc((size_t)w * h, 4);
	p->stride = (size_t)w * 4;
	wlr_buffer_init(&p->base, &pixbuf_impl, w, h);
	return p;
}

static void menu_close(struct vwm_server *s);

/* ---------- views ---------- */

static struct wlr_surface *view_surface(struct vwm_view *v)
{
	return v->xdg ? v->xdg->base->surface : v->x11->surface;
}

static const char *view_title(struct vwm_view *v)
{
	const char *t = v->xdg ? v->xdg->title : v->x11->title;
	return t ? t : "";
}

static const char *view_app_id(struct vwm_view *v)
{
	const char *a = v->xdg ? v->xdg->app_id : v->x11->class;
	return a ? a : "";
}

static void view_geometry(struct vwm_view *v, struct wlr_box *box)
{
	if (v->xdg) {
		*box = v->xdg->base->geometry;
		return;
	}
	box->x = 0; box->y = 0;
	box->width = v->x11->width;
	box->height = v->x11->height;
}

static int title_h(struct vwm_view *v) { return v->decorated && !v->fullscreen ? TITLEBAR_H : 0; }

static void chrome_request(struct vwm_view *v)
{
	if (!v->decorated || !v->mapped)
		return;
	if (v->chrome_pending) {
		v->chrome_dirty = true;   /* ask again when the image in flight arrives */
		return;
	}
	v->chrome_pending = true;
	vwm_ipc_chrome_request(v->server->ipc, v);
}

static void chrome_layout(struct vwm_view *v)
{
	if (!v->chrome)
		return;
	struct wlr_box g;
	view_geometry(v, &g);
	int w = g.width, h = g.height;
	wlr_scene_node_set_enabled(&v->chrome->node, v->decorated && !v->fullscreen);
	wlr_scene_node_set_position(&v->title_image->node, 0, -TITLEBAR_H);
	wlr_scene_node_set_position(&v->title_fallback->node, 0, -TITLEBAR_H);
	wlr_scene_rect_set_size(v->title_fallback, w, TITLEBAR_H);
	if (v->title_image->buffer)
		wlr_scene_buffer_set_dest_size(v->title_image, w, TITLEBAR_H);
	const struct { int x, y, w, h; } r[5] = {
		{ -CHROME_GRIP, -TITLEBAR_H, CHROME_GRIP, TITLEBAR_H + h },        /* left */
		{ w, -TITLEBAR_H, CHROME_GRIP, TITLEBAR_H + h },                  /* right */
		{ 0, h, w, CHROME_GRIP },                                         /* bottom */
		{ -CHROME_GRIP, h, CHROME_GRIP, CHROME_GRIP },                    /* bottom-left */
		{ w, h, CHROME_GRIP, CHROME_GRIP },                               /* bottom-right */
	};
	for (int i = 0; i < 5; i++) {
		wlr_scene_node_set_position(&v->grips[i]->node, r[i].x, r[i].y);
		wlr_scene_buffer_set_dest_size(v->grips[i], r[i].w, r[i].h);
	}
}

static void view_enable_chrome(struct vwm_view *v)
{
	if (v->chrome)
		return;
	static const uint32_t edges[5] = {
		WLR_EDGE_LEFT, WLR_EDGE_RIGHT, WLR_EDGE_BOTTOM, WLR_EDGE_LEFT | WLR_EDGE_BOTTOM, WLR_EDGE_RIGHT | WLR_EDGE_BOTTOM,
	};
	v->chrome = wlr_scene_tree_create(v->tree);
	float plain[4] = { 0.12f, 0.12f, 0.16f, 1.0f };   /* shown until the shell's drawing arrives (or when no shell runs) */
	v->title_fallback = wlr_scene_rect_create(v->chrome, 1, TITLEBAR_H, plain);
	v->title_image = wlr_scene_buffer_create(v->chrome, NULL);
	v->title_hit = (struct chrome_hit){ .view = v, .kind = HIT_TITLE };
	v->title_image->node.data = &v->title_hit;
	/* The fallback rect takes no input, so the (still empty) image node cannot be hit either: give it a transparent
	 * buffer until the real one comes. */
	struct pixbuf *blank = pixbuf_create(1, 1, NULL);
	wlr_scene_buffer_set_buffer(v->title_image, &blank->base);
	wlr_buffer_drop(&blank->base);
	for (int i = 0; i < 5; i++) {
		struct pixbuf *pb = pixbuf_create(1, 1, NULL);
		v->grips[i] = wlr_scene_buffer_create(v->chrome, &pb->base);
		wlr_buffer_drop(&pb->base);
		v->grip_hit[i] = (struct chrome_hit){ .view = v, .kind = HIT_GRIP, .edges = edges[i] };
		v->grips[i]->node.data = &v->grip_hit[i];
	}
	chrome_layout(v);
}

void vwm_view_redraw_chrome(struct vwm_view *v)
{
	v->chrome_pending = false;
	v->chrome_dirty = false;
	chrome_request(v);
}

void vwm_view_set_chrome(struct vwm_view *v, int w, int h, void *bgra)
{
	wlr_log(WLR_DEBUG, "titlebar image for window %u: %dx%d", v->id, w, h);
	struct pixbuf *pb = pixbuf_create(w, h, bgra);
	v->chrome_pending = false;
	if (v->title_image) {
		wlr_scene_buffer_set_buffer(v->title_image, &pb->base);
		wlr_scene_node_set_enabled(&v->title_fallback->node, false);
		chrome_layout(v);
	}
	wlr_buffer_drop(&pb->base);
	if (v->chrome_dirty) {
		v->chrome_dirty = false;
		chrome_request(v);
	}
}

void vwm_view_get_info(struct vwm_view *v, struct vwm_view_info *out)
{
	struct wlr_box g;
	view_geometry(v, &g);
	*out = (struct vwm_view_info){
		.id = v->id, .title = view_title(v), .app_id = view_app_id(v),
		.active = v->server->focused == v, .minimized = v->minimized, .maximized = v->maximized,
		.fullscreen = v->fullscreen, .x11 = v->x11 != NULL,
		.x = v->x, .y = v->y, .width = g.width, .height = g.height,
		.decorated = v->decorated, .pressed = v->pressed,
		.hover = v->hover == BTN_MIN ? "min" : v->hover == BTN_MAX ? "max" : v->hover == BTN_CLOSE ? "close" : "none",
	};
}

struct vwm_view *vwm_find_view(struct vwm_server *s, uint32_t id)
{
	struct vwm_view *v;
	wl_list_for_each(v, &s->views, link)
		if (v->id == id)
			return v;
	return NULL;
}

void vwm_each_view(struct vwm_server *s, void (*fn)(struct vwm_view *, void *), void *data)
{
	struct vwm_view *v;
	wl_list_for_each(v, &s->views, link)
		if (v->mapped)
			fn(v, data);
}

static void view_announce(struct vwm_view *v)
{
	if (v->mapped)
		vwm_ipc_window(v->server->ipc, v);
}

static void view_move(struct vwm_view *v, int x, int y)
{
	v->x = x;
	v->y = y;
	wlr_scene_node_set_position(&v->tree->node, x, y);
}

static void view_resize(struct vwm_view *v, int w, int h)
{
	if (v->xdg)
		wlr_xdg_toplevel_set_size(v->xdg, w, h);
	else
		wlr_xwayland_surface_configure(v->x11, v->x, v->y, w, h);
}

static void update_handle_state(struct vwm_view *v)
{
	if (!v->handle)
		return;
	wlr_foreign_toplevel_handle_v1_set_activated(v->handle, v->server->focused == v);
	wlr_foreign_toplevel_handle_v1_set_minimized(v->handle, v->minimized);
	wlr_foreign_toplevel_handle_v1_set_maximized(v->handle, v->maximized);
	wlr_foreign_toplevel_handle_v1_set_fullscreen(v->handle, v->fullscreen);
}

static void focus_view(struct vwm_view *v)
{
	struct vwm_server *s = v->server;
	struct vwm_view *prev = s->focused;
	if (prev == v)
		return;
	struct wlr_seat *seat = s->seat;
	if (prev) {
		if (prev->xdg)
			wlr_xdg_toplevel_set_activated(prev->xdg, false);
		else
			wlr_xwayland_surface_activate(prev->x11, false);
	}
	s->focused = v;
	wlr_scene_node_raise_to_top(&v->tree->node);
	wl_list_remove(&v->link);
	wl_list_insert(&s->views, &v->link);
	if (v->xdg)
		wlr_xdg_toplevel_set_activated(v->xdg, true);
	else {
		wlr_xwayland_surface_activate(v->x11, true);
		wlr_xwayland_surface_restack(v->x11, NULL, XCB_STACK_MODE_ABOVE);
	}
	struct wlr_keyboard *kb = wlr_seat_get_keyboard(seat);
	if (kb)
		wlr_seat_keyboard_notify_enter(seat, view_surface(v), kb->keycodes, kb->num_keycodes, &kb->modifiers);
	if (prev) {
		update_handle_state(prev);
		view_announce(prev);
		chrome_request(prev);
	}
	update_handle_state(v);
	view_announce(v);
	chrome_request(v);
}

void vwm_view_activate(struct vwm_view *v)
{
	if (!v->mapped)
		return;
	if (v->minimized)
		vwm_view_set_minimized(v, false);
	/* if it is already the focused view, still make sure it is on top */
	if (v->server->focused == v)
		wlr_scene_node_raise_to_top(&v->tree->node);
	else
		focus_view(v);
}

static void focus_next_after_loss(struct vwm_server *s)
{
	struct vwm_view *v;
	wl_list_for_each(v, &s->views, link)
		if (v->mapped && !v->minimized) {
			focus_view(v);
			return;
		}
	s->focused = NULL;
	wlr_seat_keyboard_notify_clear_focus(s->seat);
}

void vwm_view_set_minimized(struct vwm_view *v, bool minimized)
{
	if (v->minimized == minimized || !v->mapped)
		return;
	v->minimized = minimized;
	wlr_scene_node_set_enabled(&v->tree->node, !minimized);
	if (v->x11)
		wlr_xwayland_surface_set_minimized(v->x11, minimized);
	if (minimized && v->server->focused == v) {
		if (v->xdg)
			wlr_xdg_toplevel_set_activated(v->xdg, false);
		else
			wlr_xwayland_surface_activate(v->x11, false);
		v->server->focused = NULL;
		focus_next_after_loss(v->server);
	}
	update_handle_state(v);
	view_announce(v);
	if (!minimized)
		focus_view(v);
}

void vwm_view_set_maximized(struct vwm_view *v, bool maximized)
{
	if (v->maximized == maximized || !v->mapped)
		return;
	if (maximized) {
		struct wlr_box g;
		view_geometry(v, &g);
		v->restore = (struct wlr_box){ v->x, v->y, g.width, g.height };
		struct wlr_box area = usable_area(v->server);
		view_move(v, area.x, area.y + title_h(v));
		view_resize(v, area.width, area.height - title_h(v));
	} else {
		view_move(v, v->restore.x, v->restore.y);
		view_resize(v, v->restore.width, v->restore.height);
	}
	v->maximized = maximized;
	if (v->xdg)
		wlr_xdg_toplevel_set_maximized(v->xdg, maximized);
	else
		wlr_xwayland_surface_set_maximized(v->x11, maximized, maximized);
	update_handle_state(v);
	view_announce(v);
	chrome_layout(v);
	chrome_request(v);
}

void vwm_view_close(struct vwm_view *v)
{
	if (v->xdg)
		wlr_xdg_toplevel_send_close(v->xdg);
	else
		wlr_xwayland_surface_close(v->x11);
}

static void view_set_fullscreen(struct vwm_view *v, bool on)
{
	if (v->fullscreen == on || !v->mapped)
		return;
	struct vwm_output *o = primary_output(v->server);
	if (!o)
		return;
	if (on) {
		struct wlr_box g;
		view_geometry(v, &g);
		v->restore = (struct wlr_box){ v->x, v->y, g.width, g.height };
		struct wlr_box full;
		wlr_output_layout_get_box(v->server->output_layout, o->wlr_output, &full);
		view_move(v, full.x, full.y);
		view_resize(v, full.width, full.height);
	} else {
		view_move(v, v->restore.x, v->restore.y);
		view_resize(v, v->restore.width, v->restore.height);
	}
	v->fullscreen = on;
	chrome_layout(v);
	if (v->xdg)
		wlr_xdg_toplevel_set_fullscreen(v->xdg, on);
	else
		wlr_xwayland_surface_set_fullscreen(v->x11, on);
	update_handle_state(v);
	view_announce(v);
}

static void place_new_view(struct vwm_view *v)
{
	struct wlr_box area = usable_area(v->server);
	struct wlr_box g;
	view_geometry(v, &g);
	int w = g.width > 0 ? g.width : 800, h = g.height > 0 ? g.height : 600;
	static int cascade;
	int x = area.x + (area.width - w) / 2 + (cascade % 5) * 28;
	int y = area.y + (area.height - h) / 2 + (cascade % 5) * 28;
	cascade++;
	if (x < area.x) x = area.x;
	if (y < area.y + title_h(v)) y = area.y + title_h(v);
	view_move(v, x, y);
}

/* ----- shared by xdg and X11 ----- */

static void on_handle_activate(struct wl_listener *l, void *data)
{
	struct vwm_view *v = FROM(struct vwm_view, l, handle_activate);
	vwm_view_activate(v);
}
static void on_handle_close(struct wl_listener *l, void *data)
{
	vwm_view_close(FROM(struct vwm_view, l, handle_close));
}
static void on_handle_minimize(struct wl_listener *l, void *data)
{
	struct wlr_foreign_toplevel_handle_v1_minimized_event *e = data;
	vwm_view_set_minimized(FROM(struct vwm_view, l, handle_minimize), e->minimized);
}
static void on_handle_maximize(struct wl_listener *l, void *data)
{
	struct wlr_foreign_toplevel_handle_v1_maximized_event *e = data;
	vwm_view_set_maximized(FROM(struct vwm_view, l, handle_maximize), e->maximized);
}

static void view_mapped(struct vwm_view *v)
{
	struct vwm_server *s = v->server;
	v->mapped = true;
	v->id = ++s->next_id;
	if (v->x11) {
		v->unmanaged = v->x11->override_redirect;
		v->decorated = !v->unmanaged && !s->no_chrome && v->x11->decorations == WLR_XWAYLAND_SURFACE_DECORATIONS_ALL;
		if (v->unmanaged) {
			wlr_scene_node_set_enabled(&v->tree->node, true);
			view_move(v, v->x11->x, v->x11->y);
			return;
		}
	}
	wlr_scene_node_set_enabled(&v->tree->node, true);
	if (v->decorated)
		view_enable_chrome(v);
	place_new_view(v);

	v->handle = wlr_foreign_toplevel_handle_v1_create(s->toplevels);
	wlr_foreign_toplevel_handle_v1_set_title(v->handle, view_title(v));
	wlr_foreign_toplevel_handle_v1_set_app_id(v->handle, view_app_id(v));
	v->handle_activate.notify = on_handle_activate;
	wl_signal_add(&v->handle->events.request_activate, &v->handle_activate);
	v->handle_close.notify = on_handle_close;
	wl_signal_add(&v->handle->events.request_close, &v->handle_close);
	v->handle_minimize.notify = on_handle_minimize;
	wl_signal_add(&v->handle->events.request_minimize, &v->handle_minimize);
	v->handle_maximize.notify = on_handle_maximize;
	wl_signal_add(&v->handle->events.request_maximize, &v->handle_maximize);

	focus_view(v);
	view_announce(v);
}

static void view_unmapped(struct vwm_view *v)
{
	struct vwm_server *s = v->server;
	if (!v->mapped)
		return;
	v->mapped = false;
	if (v->unmanaged) {
		wlr_scene_node_set_enabled(&v->tree->node, false);
		return;
	}
	vwm_ipc_closed(s->ipc, v->id);
	if (v->handle) {
		wl_list_remove(&v->handle_activate.link);
		wl_list_remove(&v->handle_close.link);
		wl_list_remove(&v->handle_minimize.link);
		wl_list_remove(&v->handle_maximize.link);
		wlr_foreign_toplevel_handle_v1_destroy(v->handle);
		v->handle = NULL;
	}
	if (s->grabbed == v) {
		s->mode = CURSOR_PASSTHROUGH;
		s->grabbed = NULL;
		s->sticky_grab = false;
	}
	if (s->menu_view == v)
		menu_close(s);
	if (s->focused == v) {
		s->focused = NULL;
		focus_next_after_loss(s);
	}
}

static void view_titles_changed(struct vwm_view *v)
{
	if (!v->mapped)
		return;
	if (v->handle) {
		wlr_foreign_toplevel_handle_v1_set_title(v->handle, view_title(v));
		wlr_foreign_toplevel_handle_v1_set_app_id(v->handle, view_app_id(v));
	}
	view_announce(v);
	chrome_request(v);
}

/* The client's size may have changed: the titlebar follows its width. */
static void view_committed(struct vwm_view *v)
{
	if (!v->mapped)
		return;
	struct wlr_box g;
	view_geometry(v, &g);
	if (g.width == v->last_w && g.height == v->last_h)
		return;
	v->last_w = g.width;
	v->last_h = g.height;
	chrome_layout(v);
	chrome_request(v);
}

/* ---------- xdg-shell toplevels ---------- */

static void xdg_map(struct wl_listener *l, void *data) { view_mapped(FROM(struct vwm_view, l, map)); }
static void xdg_unmap(struct wl_listener *l, void *data) { view_unmapped(FROM(struct vwm_view, l, unmap)); }

static void xdg_commit(struct wl_listener *l, void *data)
{
	struct vwm_view *v = FROM(struct vwm_view, l, commit);
	view_committed(v);
	if (v->xdg->base->initial_commit) {
		wlr_xdg_toplevel_set_wm_capabilities(v->xdg, WLR_XDG_TOPLEVEL_WM_CAPABILITIES_MAXIMIZE |
			WLR_XDG_TOPLEVEL_WM_CAPABILITIES_FULLSCREEN | WLR_XDG_TOPLEVEL_WM_CAPABILITIES_MINIMIZE);
		wlr_xdg_toplevel_set_size(v->xdg, 0, 0);   /* let the client pick */
		if (v->decoration)
			wlr_xdg_toplevel_decoration_v1_set_mode(v->decoration, DECORATION_MODE(v->server));
	}
}

static void xdg_destroy(struct wl_listener *l, void *data)
{
	struct vwm_view *v = FROM(struct vwm_view, l, destroy);
	view_unmapped(v);
	wl_list_remove(&v->map.link);
	wl_list_remove(&v->unmap.link);
	wl_list_remove(&v->commit.link);
	wl_list_remove(&v->destroy.link);
	wl_list_remove(&v->request_move.link);
	wl_list_remove(&v->request_resize.link);
	wl_list_remove(&v->request_maximize.link);
	wl_list_remove(&v->request_fullscreen.link);
	wl_list_remove(&v->request_minimize.link);
	wl_list_remove(&v->set_title.link);
	wl_list_remove(&v->set_app_id.link);
	wl_list_remove(&v->link);
	free(v);
}

static void begin_grab(struct vwm_view *v, enum cursor_mode mode, uint32_t edges)
{
	struct vwm_server *s = v->server;
	if (s->focused != v)
		return;   /* only the window the user is using may start a grab */
	s->grabbed = v;
	s->mode = mode;
	struct wlr_box g;
	view_geometry(v, &g);
	if (mode == CURSOR_MOVE) {
		s->grab_x = s->cursor->x - v->x;
		s->grab_y = s->cursor->y - v->y;
	} else {
		s->grab_box = (struct wlr_box){ v->x, v->y, g.width, g.height };
		s->grab_x = s->cursor->x;
		s->grab_y = s->cursor->y;
		s->resize_edges = edges;
	}
}

static void xdg_request_move(struct wl_listener *l, void *data) { begin_grab(FROM(struct vwm_view, l, request_move), CURSOR_MOVE, 0); }
static void xdg_request_resize(struct wl_listener *l, void *data)
{
	struct wlr_xdg_toplevel_resize_event *e = data;
	begin_grab(FROM(struct vwm_view, l, request_resize), CURSOR_RESIZE, e->edges);
}
static void xdg_request_maximize(struct wl_listener *l, void *data)
{
	struct vwm_view *v = FROM(struct vwm_view, l, request_maximize);
	bool want = v->xdg->requested.maximized;
	if (v->mapped)
		vwm_view_set_maximized(v, want);
	else if (v->xdg->base->initialized)
		wlr_xdg_surface_schedule_configure(v->xdg->base);
}
static void xdg_request_fullscreen(struct wl_listener *l, void *data)
{
	struct vwm_view *v = FROM(struct vwm_view, l, request_fullscreen);
	if (v->mapped)
		view_set_fullscreen(v, v->xdg->requested.fullscreen);
	else if (v->xdg->base->initialized)
		wlr_xdg_surface_schedule_configure(v->xdg->base);
}
static void xdg_request_minimize(struct wl_listener *l, void *data)
{
	struct vwm_view *v = FROM(struct vwm_view, l, request_minimize);
	if (v->mapped)
		vwm_view_set_minimized(v, v->xdg->requested.minimized);
	else if (v->xdg->base->initialized)
		wlr_xdg_surface_schedule_configure(v->xdg->base);
}
static void xdg_set_title(struct wl_listener *l, void *data) { view_titles_changed(FROM(struct vwm_view, l, set_title)); }
static void xdg_set_app_id(struct wl_listener *l, void *data) { view_titles_changed(FROM(struct vwm_view, l, set_app_id)); }

static void on_new_xdg_toplevel(struct wl_listener *l, void *data)
{
	struct vwm_server *s = FROM(struct vwm_server, l, new_xdg_toplevel);
	struct wlr_xdg_toplevel *top = data;
	struct vwm_view *v = calloc(1, sizeof *v);
	v->server = s;
	v->xdg = top;
	v->tree = wlr_scene_tree_create(s->layers[LAYER_WINDOWS]);
	wlr_scene_node_set_enabled(&v->tree->node, false);
	v->surface_tree = wlr_scene_xdg_surface_create(v->tree, top->base);
	v->tree->node.data = v;
	top->base->data = v->tree;
	wl_list_insert(&s->views, &v->link);

	v->map.notify = xdg_map;                       wl_signal_add(&top->base->surface->events.map, &v->map);
	v->unmap.notify = xdg_unmap;                   wl_signal_add(&top->base->surface->events.unmap, &v->unmap);
	v->commit.notify = xdg_commit;                 wl_signal_add(&top->base->surface->events.commit, &v->commit);
	v->destroy.notify = xdg_destroy;               wl_signal_add(&top->events.destroy, &v->destroy);
	v->request_move.notify = xdg_request_move;     wl_signal_add(&top->events.request_move, &v->request_move);
	v->request_resize.notify = xdg_request_resize; wl_signal_add(&top->events.request_resize, &v->request_resize);
	v->request_maximize.notify = xdg_request_maximize;     wl_signal_add(&top->events.request_maximize, &v->request_maximize);
	v->request_fullscreen.notify = xdg_request_fullscreen; wl_signal_add(&top->events.request_fullscreen, &v->request_fullscreen);
	v->request_minimize.notify = xdg_request_minimize;     wl_signal_add(&top->events.request_minimize, &v->request_minimize);
	v->set_title.notify = xdg_set_title;           wl_signal_add(&top->events.set_title, &v->set_title);
	v->set_app_id.notify = xdg_set_app_id;         wl_signal_add(&top->events.set_app_id, &v->set_app_id);
}

static void on_new_xdg_popup(struct wl_listener *l, void *data)
{
	struct wlr_xdg_popup *popup = data;
	struct wlr_xdg_surface *parent = wlr_xdg_surface_try_from_wlr_surface(popup->parent);
	if (!parent)
		return;
	struct wlr_scene_tree *parent_tree = parent->data;
	popup->base->data = wlr_scene_xdg_surface_create(parent_tree, popup->base);
}

/* Who draws the titlebar. Server-side: vwm composites the image the shell renders with the Avalonia theme
 * (docs/vwm.md). `-d` leaves every client drawing its own. */
static void decoration_destroyed(struct wl_listener *l, void *data)
{
	struct vwm_view *v = FROM(struct vwm_view, l, decoration_destroy);
	v->decoration = NULL;
	v->decorated = false;
	wl_list_remove(&v->decoration_destroy.link);
	wl_list_remove(&v->decoration_mode.link);
}

static void decoration_request_mode(struct wl_listener *l, void *data)
{
	struct vwm_view *v = FROM(struct vwm_view, l, decoration_mode);
	if (v->decoration && v->xdg->base->initialized)
		wlr_xdg_toplevel_decoration_v1_set_mode(v->decoration, DECORATION_MODE(v->server));
}

static void on_new_decoration(struct wl_listener *l, void *data)
{
	struct wlr_xdg_toplevel_decoration_v1 *d = data;
	struct wlr_scene_tree *tree = d->toplevel->base->data;
	struct vwm_view *v = tree ? tree->node.data : NULL;
	if (!v)
		return;
	v->decoration = d;
	v->decorated = !v->server->no_chrome;
	v->decoration_destroy.notify = decoration_destroyed;
	wl_signal_add(&d->events.destroy, &v->decoration_destroy);
	v->decoration_mode.notify = decoration_request_mode;
	wl_signal_add(&d->events.request_mode, &v->decoration_mode);
	if (d->toplevel->base->initialized)
		wlr_xdg_toplevel_decoration_v1_set_mode(d, DECORATION_MODE(v->server));   /* otherwise the first commit does it */
}

/* ---------- XWayland ---------- */

static void x11_map(struct wl_listener *l, void *data) { view_mapped(FROM(struct vwm_view, l, map)); }
static void x11_unmap(struct wl_listener *l, void *data) { view_unmapped(FROM(struct vwm_view, l, unmap)); }

static void x11_commit(struct wl_listener *l, void *data) { view_committed(FROM(struct vwm_view, l, x11_commit)); }

static void x11_associate(struct wl_listener *l, void *data)
{
	struct vwm_view *v = FROM(struct vwm_view, l, associate);
	struct wlr_surface *surface = v->x11->surface;
	v->surface_tree = wlr_scene_subsurface_tree_create(v->tree, surface);
	v->map.notify = x11_map;     wl_signal_add(&surface->events.map, &v->map);
	v->unmap.notify = x11_unmap; wl_signal_add(&surface->events.unmap, &v->unmap);
	v->x11_commit.notify = x11_commit;
	wl_signal_add(&surface->events.commit, &v->x11_commit);
}

static void x11_dissociate(struct wl_listener *l, void *data)
{
	struct vwm_view *v = FROM(struct vwm_view, l, dissociate);
	view_unmapped(v);
	wl_list_remove(&v->map.link);
	wl_list_remove(&v->unmap.link);
	wl_list_remove(&v->x11_commit.link);
	if (v->surface_tree) {
		wlr_scene_node_destroy(&v->surface_tree->node);
		v->surface_tree = NULL;
	}
}

static void x11_request_configure(struct wl_listener *l, void *data)
{
	struct vwm_view *v = FROM(struct vwm_view, l, request_configure);
	struct wlr_xwayland_surface_configure_event *e = data;
	if (!v->mapped || v->x11->override_redirect) {
		wlr_xwayland_surface_configure(v->x11, e->x, e->y, e->width, e->height);   /* not shown yet: grant it */
		v->x = e->x; v->y = e->y;
		if (v->tree)
			wlr_scene_node_set_position(&v->tree->node, e->x, e->y);
		return;
	}
	wlr_xwayland_surface_configure(v->x11, v->x, v->y, e->width, e->height);   /* we decide where windows go */
}
static void x11_request_activate(struct wl_listener *l, void *data) { vwm_view_activate(FROM(struct vwm_view, l, request_activate)); }
static void x11_request_close(struct wl_listener *l, void *data) { vwm_view_close(FROM(struct vwm_view, l, request_close)); }
static void x11_request_move(struct wl_listener *l, void *data) { begin_grab(FROM(struct vwm_view, l, request_move), CURSOR_MOVE, 0); }
static void x11_request_resize(struct wl_listener *l, void *data)
{
	struct wlr_xwayland_resize_event *e = data;
	begin_grab(FROM(struct vwm_view, l, request_resize), CURSOR_RESIZE, e->edges);
}
static void x11_request_maximize(struct wl_listener *l, void *data)
{
	struct vwm_view *v = FROM(struct vwm_view, l, request_maximize);
	vwm_view_set_maximized(v, v->x11->maximized_horz && v->x11->maximized_vert);
}
static void x11_request_fullscreen(struct wl_listener *l, void *data)
{
	struct vwm_view *v = FROM(struct vwm_view, l, request_fullscreen);
	view_set_fullscreen(v, v->x11->fullscreen);
}
static void x11_request_minimize(struct wl_listener *l, void *data)
{
	struct wlr_xwayland_minimize_event *e = data;
	vwm_view_set_minimized(FROM(struct vwm_view, l, request_minimize), e->minimize);
}
static void x11_title(struct wl_listener *l, void *data) { view_titles_changed(FROM(struct vwm_view, l, x11_set_title)); }
static void x11_class(struct wl_listener *l, void *data) { view_titles_changed(FROM(struct vwm_view, l, x11_set_class)); }

static void x11_destroy(struct wl_listener *l, void *data)
{
	struct vwm_view *v = FROM(struct vwm_view, l, destroy);
	view_unmapped(v);
	wl_list_remove(&v->associate.link);
	wl_list_remove(&v->dissociate.link);
	wl_list_remove(&v->destroy.link);
	wl_list_remove(&v->request_configure.link);
	wl_list_remove(&v->request_activate.link);
	wl_list_remove(&v->request_close.link);
	wl_list_remove(&v->request_move.link);
	wl_list_remove(&v->request_resize.link);
	wl_list_remove(&v->request_maximize.link);
	wl_list_remove(&v->request_fullscreen.link);
	wl_list_remove(&v->request_minimize.link);
	wl_list_remove(&v->x11_set_title.link);
	wl_list_remove(&v->x11_set_class.link);
	wl_list_remove(&v->link);
	wlr_scene_node_destroy(&v->tree->node);
	free(v);
}

static void on_new_x11(struct wl_listener *l, void *data)
{
	struct vwm_server *s = FROM(struct vwm_server, l, new_x11);
	struct wlr_xwayland_surface *xs = data;
	struct vwm_view *v = calloc(1, sizeof *v);
	v->server = s;
	v->x11 = xs;
	/* menus and tooltips (override-redirect) sit above windows and are not listed; ordinary windows are views */
	v->tree = wlr_scene_tree_create(s->layers[xs->override_redirect ? LAYER_TOP : LAYER_WINDOWS]);
	wlr_scene_node_set_enabled(&v->tree->node, false);
	v->tree->node.data = v;
	wl_list_insert(&s->views, &v->link);

	v->associate.notify = x11_associate;           wl_signal_add(&xs->events.associate, &v->associate);
	v->dissociate.notify = x11_dissociate;         wl_signal_add(&xs->events.dissociate, &v->dissociate);
	v->destroy.notify = x11_destroy;               wl_signal_add(&xs->events.destroy, &v->destroy);
	v->request_configure.notify = x11_request_configure; wl_signal_add(&xs->events.request_configure, &v->request_configure);
	v->request_activate.notify = x11_request_activate;   wl_signal_add(&xs->events.request_activate, &v->request_activate);
	v->request_close.notify = x11_request_close;         wl_signal_add(&xs->events.request_close, &v->request_close);
	v->request_move.notify = x11_request_move;           wl_signal_add(&xs->events.request_move, &v->request_move);
	v->request_resize.notify = x11_request_resize;       wl_signal_add(&xs->events.request_resize, &v->request_resize);
	v->request_maximize.notify = x11_request_maximize;   wl_signal_add(&xs->events.request_maximize, &v->request_maximize);
	v->request_fullscreen.notify = x11_request_fullscreen; wl_signal_add(&xs->events.request_fullscreen, &v->request_fullscreen);
	v->request_minimize.notify = x11_request_minimize;   wl_signal_add(&xs->events.request_minimize, &v->request_minimize);
	v->x11_set_title.notify = x11_title;                 wl_signal_add(&xs->events.set_title, &v->x11_set_title);
	v->x11_set_class.notify = x11_class;                 wl_signal_add(&xs->events.set_class, &v->x11_set_class);
}

static void on_x11_ready(struct wl_listener *l, void *data)
{
	struct vwm_server *s = FROM(struct vwm_server, l, x11_ready);
	wlr_xwayland_set_seat(s->xwayland, s->seat);
	wlr_log(WLR_INFO, "XWayland ready on %s", s->xwayland->display_name);
}

/* ---------- panels (layer-shell) ---------- */

static void arrange_layers(struct vwm_server *s, struct vwm_output *o)
{
	struct wlr_box full;
	wlr_output_layout_get_box(s->output_layout, o->wlr_output, &full);
	struct wlr_box usable = full;
	struct vwm_layer *ls;
	/* exclusive zones first (they shrink what is left), from the topmost layer down */
	for (int pass = 0; pass < 2; pass++)
		wl_list_for_each(ls, &s->layer_surfaces, link) {
			struct wlr_layer_surface_v1 *surf = ls->surface;
			if (surf->output != o->wlr_output || !surf->initialized)
				continue;
			bool exclusive = surf->current.exclusive_zone > 0;
			if ((pass == 0) != exclusive)
				continue;
			wlr_scene_layer_surface_v1_configure(ls->scene, &full, &usable);
		}
	o->usable = usable;
	/* windows that were maximized follow the panels */
	struct vwm_view *v;
	wl_list_for_each(v, &s->views, link)
		if (v->mapped && v->maximized) {
			view_move(v, usable.x, usable.y);
			view_resize(v, usable.width, usable.height);
		}
}

static void layer_commit(struct wl_listener *l, void *data)
{
	struct vwm_layer *ls = FROM(struct vwm_layer, l, commit);
	struct wlr_layer_surface_v1 *surf = ls->surface;
	if (!surf->initial_commit && !surf->surface->mapped)
		return;
	if (!surf->output)
		return;
	struct vwm_output *o = surf->output->data;
	arrange_layers(ls->server, o);
}

static void layer_map(struct wl_listener *l, void *data)
{
	struct vwm_layer *ls = FROM(struct vwm_layer, l, map);
	struct wlr_layer_surface_v1 *surf = ls->surface;
	if (surf->current.keyboard_interactive == ZWLR_LAYER_SURFACE_V1_KEYBOARD_INTERACTIVITY_EXCLUSIVE) {
		struct wlr_keyboard *kb = wlr_seat_get_keyboard(ls->server->seat);
		if (kb)
			wlr_seat_keyboard_notify_enter(ls->server->seat, surf->surface, kb->keycodes, kb->num_keycodes, &kb->modifiers);
	}
}

static void layer_unmap(struct wl_listener *l, void *data)
{
	struct vwm_layer *ls = FROM(struct vwm_layer, l, unmap);
	struct vwm_server *s = ls->server;
	if (s->seat->keyboard_state.focused_surface == ls->surface->surface) {
		if (s->focused)
			focus_next_after_loss(s);
		else
			wlr_seat_keyboard_notify_clear_focus(s->seat);
	}
}

static void layer_destroy(struct wl_listener *l, void *data)
{
	struct vwm_layer *ls = FROM(struct vwm_layer, l, destroy);
	struct vwm_output *o = ls->surface->output ? ls->surface->output->data : NULL;
	wl_list_remove(&ls->link);
	wl_list_remove(&ls->map.link);
	wl_list_remove(&ls->unmap.link);
	wl_list_remove(&ls->commit.link);
	wl_list_remove(&ls->destroy.link);
	struct vwm_server *s = ls->server;
	free(ls);
	if (o)
		arrange_layers(s, o);
}

static void on_new_layer(struct wl_listener *l, void *data)
{
	struct vwm_server *s = FROM(struct vwm_server, l, new_layer);
	struct wlr_layer_surface_v1 *surf = data;
	if (!surf->output) {
		struct vwm_output *o = primary_output(s);
		if (!o) {
			wlr_layer_surface_v1_destroy(surf);
			return;
		}
		surf->output = o->wlr_output;
	}
	static const enum layer_index map[] = {
		[ZWLR_LAYER_SHELL_V1_LAYER_BACKGROUND] = LAYER_BACKGROUND, [ZWLR_LAYER_SHELL_V1_LAYER_BOTTOM] = LAYER_BOTTOM,
		[ZWLR_LAYER_SHELL_V1_LAYER_TOP] = LAYER_TOP, [ZWLR_LAYER_SHELL_V1_LAYER_OVERLAY] = LAYER_OVERLAY,
	};
	struct vwm_layer *ls = calloc(1, sizeof *ls);
	ls->server = s;
	ls->surface = surf;
	ls->scene = wlr_scene_layer_surface_v1_create(s->layers[map[surf->pending.layer]], surf);
	surf->data = ls->scene->tree;
	wl_list_insert(&s->layer_surfaces, &ls->link);
	ls->map.notify = layer_map;         wl_signal_add(&surf->surface->events.map, &ls->map);
	ls->unmap.notify = layer_unmap;     wl_signal_add(&surf->surface->events.unmap, &ls->unmap);
	ls->commit.notify = layer_commit;   wl_signal_add(&surf->surface->events.commit, &ls->commit);
	ls->destroy.notify = layer_destroy; wl_signal_add(&surf->events.destroy, &ls->destroy);
}

/* ---------- outputs ---------- */

static void output_frame(struct wl_listener *l, void *data)
{
	struct vwm_output *o = FROM(struct vwm_output, l, frame);
	wlr_scene_output_commit(o->scene_output, NULL);
	struct timespec now;
	clock_gettime(CLOCK_MONOTONIC, &now);
	wlr_scene_output_send_frame_done(o->scene_output, &now);
}

static void output_request_state(struct wl_listener *l, void *data)
{
	struct vwm_output *o = FROM(struct vwm_output, l, request_state);
	const struct wlr_output_event_request_state *e = data;
	wlr_output_commit_state(o->wlr_output, e->state);
}

static void output_destroy(struct wl_listener *l, void *data)
{
	struct vwm_output *o = FROM(struct vwm_output, l, destroy);
	wl_list_remove(&o->frame.link);
	wl_list_remove(&o->request_state.link);
	wl_list_remove(&o->destroy.link);
	wl_list_remove(&o->link);
	free(o);
}

static void on_new_output(struct wl_listener *l, void *data)
{
	struct vwm_server *s = FROM(struct vwm_server, l, new_output);
	struct wlr_output *out = data;
	if (!wlr_output_init_render(out, s->allocator, s->renderer))
		return;
	struct wlr_output_state state;
	wlr_output_state_init(&state);
	wlr_output_state_set_enabled(&state, true);
	struct wlr_output_mode *mode = wlr_output_preferred_mode(out);
	if (mode)
		wlr_output_state_set_mode(&state, mode);
	wlr_output_commit_state(out, &state);
	wlr_output_state_finish(&state);

	struct vwm_output *o = calloc(1, sizeof *o);
	o->server = s;
	o->wlr_output = out;
	out->data = o;
	o->frame.notify = output_frame;               wl_signal_add(&out->events.frame, &o->frame);
	o->request_state.notify = output_request_state; wl_signal_add(&out->events.request_state, &o->request_state);
	o->destroy.notify = output_destroy;           wl_signal_add(&out->events.destroy, &o->destroy);
	wl_list_insert(&s->outputs, &o->link);

	struct wlr_output_layout_output *lo = wlr_output_layout_add_auto(s->output_layout, out);
	o->scene_output = wlr_scene_output_create(s->scene, out);
	wlr_scene_output_layout_add_output(s->scene_layout, lo, o->scene_output);
	struct wlr_box box;
	wlr_output_layout_get_box(s->output_layout, out, &box);
	o->usable = box;
	wlr_log(WLR_INFO, "output %s %dx%d", out->name, box.width, box.height);
}

/* ---------- input ---------- */

static struct vwm_view *view_at(struct vwm_server *s, double lx, double ly, struct wlr_surface **surface, double *sx, double *sy)
{
	struct wlr_scene_node *node = wlr_scene_node_at(&s->scene->tree.node, lx, ly, sx, sy);
	if (!node || node->type != WLR_SCENE_NODE_BUFFER)
		return NULL;
	struct wlr_scene_buffer *buf = wlr_scene_buffer_from_node(node);
	struct wlr_scene_surface *ss = wlr_scene_surface_try_from_buffer(buf);
	if (!ss)
		return NULL;
	*surface = ss->surface;
	struct wlr_scene_tree *tree = node->parent;
	while (tree && !tree->node.data)
		tree = tree->node.parent;
	return tree ? tree->node.data : NULL;
}

/* ---------- the window menu ---------- */

static unsigned menu_enabled(struct vwm_view *v)
{
	bool free_form = !v->maximized && !v->fullscreen;
	unsigned m = (1u << 3) | (1u << 5);                      /* Minimize, Close */
	if (v->maximized) m |= 1u << 0;                          /* Restore */
	if (free_form) m |= (1u << 1) | (1u << 2) | (1u << 4);   /* Move, Size, Maximize */
	return m;
}

static void menu_request(struct vwm_server *s)
{
	if (!s->menu_view)
		return;
	if (s->menu_pending) {
		s->menu_dirty = true;
		return;
	}
	s->menu_pending = true;
	vwm_ipc_menu_request(s->ipc, s->menu_view, menu_enabled(s->menu_view), s->menu_hover);
}

static void menu_close(struct vwm_server *s)
{
	if (!s->menu_tree)
		return;
	wlr_scene_node_destroy(&s->menu_tree->node);
	s->menu_tree = NULL;
	s->menu_image = NULL;
	s->menu_fallback = NULL;
	s->menu_view = NULL;
	s->menu_pending = s->menu_dirty = false;
	s->menu_hover = -1;
}

static void menu_open(struct vwm_view *v, int x, int y)
{
	struct vwm_server *s = v->server;
	menu_close(s);
	struct vwm_output *o = primary_output(s);
	if (o) {   /* keep it on the screen */
		struct wlr_box full;
		wlr_output_layout_get_box(s->output_layout, o->wlr_output, &full);
		if (x + MENU_W > full.x + full.width) x = full.x + full.width - MENU_W;
		if (y + MENU_HEIGHT > full.y + full.height) y = full.y + full.height - MENU_HEIGHT;
		if (x < full.x) x = full.x;
		if (y < full.y) y = full.y;
	}
	s->menu_tree = wlr_scene_tree_create(s->layers[LAYER_OVERLAY]);
	wlr_scene_node_set_position(&s->menu_tree->node, x, y);
	float plain[4] = { 0.12f, 0.12f, 0.16f, 1.0f };
	s->menu_fallback = wlr_scene_rect_create(s->menu_tree, MENU_W, MENU_HEIGHT, plain);   /* until the shell's drawing arrives */
	struct pixbuf *blank = pixbuf_create(1, 1, NULL);
	s->menu_image = wlr_scene_buffer_create(s->menu_tree, &blank->base);
	wlr_buffer_drop(&blank->base);
	wlr_scene_buffer_set_dest_size(s->menu_image, MENU_W, MENU_HEIGHT);
	s->menu_hit = (struct chrome_hit){ .view = v, .kind = HIT_MENU };
	s->menu_image->node.data = &s->menu_hit;
	s->menu_view = v;
	s->menu_hover = -1;
	menu_request(s);
}

void vwm_view_open_menu(struct vwm_view *v)
{
	if (v->mapped)
		menu_open(v, v->x, v->y);
}

void vwm_view_set_menu(struct vwm_view *v, int w, int h, void *bgra)
{
	struct vwm_server *s = v->server;
	wlr_log(WLR_DEBUG, "menu image for window %u: %dx%d", v->id, w, h);
	if (s->menu_view != v || !s->menu_image) {   /* closed while it was being drawn */
		free(bgra);
		return;
	}
	struct pixbuf *pb = pixbuf_create(w, h, bgra);
	wlr_scene_buffer_set_buffer(s->menu_image, &pb->base);
	wlr_scene_buffer_set_dest_size(s->menu_image, MENU_W, MENU_HEIGHT);
	wlr_scene_node_set_enabled(&s->menu_fallback->node, false);
	wlr_buffer_drop(&pb->base);
	s->menu_pending = false;
	if (s->menu_dirty) {
		s->menu_dirty = false;
		menu_request(s);
	}
}

static int menu_row_at(double ny)
{
	int row = (int)((ny - MENU_PAD) / MENU_ROW_H);
	return ny < MENU_PAD || row < 0 || row >= MENU_ROWS ? -1 : row;
}

static void menu_invoke(struct vwm_server *s, int row)
{
	struct vwm_view *v = s->menu_view;
	if (!v || row < 0 || !(menu_enabled(v) & (1u << row)))
		return;
	menu_close(s);
	switch (row) {
	case 0: vwm_view_set_maximized(v, false); break;
	case 1:   /* Move: the window follows the pointer until the next click */
		begin_grab(v, CURSOR_MOVE, 0);
		s->sticky_grab = s->mode != CURSOR_PASSTHROUGH;
		break;
	case 2: {   /* Size: from the corner nearest the pointer */
		struct wlr_box g;
		view_geometry(v, &g);
		uint32_t edges = (s->cursor->x < v->x + g.width / 2 ? WLR_EDGE_LEFT : WLR_EDGE_RIGHT) |
			(s->cursor->y < v->y + g.height / 2 ? WLR_EDGE_TOP : WLR_EDGE_BOTTOM);
		begin_grab(v, CURSOR_RESIZE, edges);
		s->sticky_grab = s->mode != CURSOR_PASSTHROUGH;
		break;
	}
	case 3: vwm_view_set_minimized(v, true); break;
	case 4: vwm_view_set_maximized(v, true); break;
	case 5: vwm_view_close(v); break;
	}
}

static enum chrome_button button_at(struct vwm_view *v, double lx)
{
	struct wlr_box g;
	view_geometry(v, &g);
	double from_right = g.width - lx;
	if (from_right < 0 || from_right >= 3 * CHROME_BUTTON_W)
		return BTN_NONE;
	if (from_right < CHROME_BUTTON_W) return BTN_CLOSE;
	if (from_right < 2 * CHROME_BUTTON_W) return BTN_MAX;
	return BTN_MIN;
}

static uint32_t title_edges(struct vwm_view *v, double lx, double ly)
{
	struct wlr_box g;
	view_geometry(v, &g);
	if (v->maximized || ly >= CHROME_RESIZE_TOP)
		return 0;
	uint32_t edges = WLR_EDGE_TOP;
	if (lx < CHROME_GRIP + 2) edges |= WLR_EDGE_LEFT;
	else if (lx > g.width - CHROME_GRIP - 2) edges |= WLR_EDGE_RIGHT;
	return edges;
}

static const char *edge_cursor(uint32_t e)
{
	switch (e) {
	case WLR_EDGE_TOP: return "n-resize";
	case WLR_EDGE_BOTTOM: return "s-resize";
	case WLR_EDGE_LEFT: return "w-resize";
	case WLR_EDGE_RIGHT: return "e-resize";
	case WLR_EDGE_TOP | WLR_EDGE_LEFT: return "nw-resize";
	case WLR_EDGE_TOP | WLR_EDGE_RIGHT: return "ne-resize";
	case WLR_EDGE_BOTTOM | WLR_EDGE_LEFT: return "sw-resize";
	case WLR_EDGE_BOTTOM | WLR_EDGE_RIGHT: return "se-resize";
	default: return "default";
	}
}

static void set_hover(struct vwm_server *s, struct vwm_view *v, enum chrome_button b)
{
	if (s->hover_view && s->hover_view != v) {
		struct vwm_view *old = s->hover_view;
		s->hover_view = NULL;
		if (old->hover != BTN_NONE) {
			old->hover = BTN_NONE;
			chrome_request(old);
		}
	}
	s->hover_view = v;
	if (v && v->hover != b) {
		v->hover = b;
		chrome_request(v);
	}
}

/* The pointer is over a titlebar or grip. Returns true: no client gets the pointer. */
static bool chrome_motion(struct vwm_server *s, struct chrome_hit *hit, double nx, double ny)
{
	struct vwm_view *v = hit->view;
	wlr_seat_pointer_clear_focus(s->seat);
	if (hit->kind == HIT_MENU) {
		int row = menu_row_at(ny);
		wlr_cursor_set_xcursor(s->cursor, s->xcursor, "default");
		if (row != s->menu_hover) {
			s->menu_hover = row;
			menu_request(s);
		}
		return true;
	}
	if (hit->kind == HIT_GRIP) {
		set_hover(s, NULL, BTN_NONE);
		wlr_cursor_set_xcursor(s->cursor, s->xcursor, edge_cursor(hit->edges));
		return true;
	}
	uint32_t edges = title_edges(v, nx, ny);
	wlr_cursor_set_xcursor(s->cursor, s->xcursor, edge_cursor(edges));
	set_hover(s, v, edges ? BTN_NONE : button_at(v, nx));
	return true;
}

static struct chrome_hit *chrome_under_cursor(struct vwm_server *s, double *nx, double *ny)
{
	struct wlr_scene_node *node = wlr_scene_node_at(&s->scene->tree.node, s->cursor->x, s->cursor->y, nx, ny);
	if (node && node->type == WLR_SCENE_NODE_BUFFER && node->data)
		return node->data;
	return NULL;
}

static void process_motion(struct vwm_server *s, uint32_t time)
{
	if (s->mode == CURSOR_MOVE && s->grabbed) {
		view_move(s->grabbed, (int)(s->cursor->x - s->grab_x), (int)(s->cursor->y - s->grab_y));
		return;
	}
	if (s->mode == CURSOR_RESIZE && s->grabbed) {
		struct vwm_view *v = s->grabbed;
		double dx = s->cursor->x - s->grab_x, dy = s->cursor->y - s->grab_y;
		int x = s->grab_box.x, y = s->grab_box.y, w = s->grab_box.width, h = s->grab_box.height;
		if (s->resize_edges & WLR_EDGE_TOP) { y += (int)dy; h -= (int)dy; }
		else if (s->resize_edges & WLR_EDGE_BOTTOM) h += (int)dy;
		if (s->resize_edges & WLR_EDGE_LEFT) { x += (int)dx; w -= (int)dx; }
		else if (s->resize_edges & WLR_EDGE_RIGHT) w += (int)dx;
		if (w < 120) w = 120;
		if (h < 60) h = 60;
		view_move(v, x, y);
		view_resize(v, w, h);
		return;
	}
	double cx, cy;
	if (s->mode == CURSOR_PASSTHROUGH) {
		struct chrome_hit *hit = chrome_under_cursor(s, &cx, &cy);
		if (hit && chrome_motion(s, hit, cx, cy))
			return;
		set_hover(s, NULL, BTN_NONE);
		if (s->menu_view && s->menu_hover != -1) {
			s->menu_hover = -1;
			menu_request(s);
		}
	}
	double sx, sy;
	struct wlr_surface *surface = NULL;
	struct vwm_view *v = view_at(s, s->cursor->x, s->cursor->y, &surface, &sx, &sy);
	if (!v) {
		/* panels are not views: find a plain surface under the pointer for them */
		struct wlr_scene_node *node = wlr_scene_node_at(&s->scene->tree.node, s->cursor->x, s->cursor->y, &sx, &sy);
		if (node && node->type == WLR_SCENE_NODE_BUFFER) {
			struct wlr_scene_surface *ss = wlr_scene_surface_try_from_buffer(wlr_scene_buffer_from_node(node));
			if (ss)
				surface = ss->surface;
		}
		if (!surface)
			wlr_cursor_set_xcursor(s->cursor, s->xcursor, "default");
	}
	if (surface) {
		wlr_seat_pointer_notify_enter(s->seat, surface, sx, sy);
		wlr_seat_pointer_notify_motion(s->seat, time, sx, sy);
	} else {
		wlr_seat_pointer_clear_focus(s->seat);
	}
}

static void on_cursor_motion(struct wl_listener *l, void *data)
{
	struct vwm_server *s = FROM(struct vwm_server, l, cursor_motion);
	struct wlr_pointer_motion_event *e = data;
	wlr_cursor_move(s->cursor, &e->pointer->base, e->delta_x, e->delta_y);
	process_motion(s, e->time_msec);
}

static void on_cursor_motion_absolute(struct wl_listener *l, void *data)
{
	struct vwm_server *s = FROM(struct vwm_server, l, cursor_motion_absolute);
	struct wlr_pointer_motion_absolute_event *e = data;
	wlr_cursor_warp_absolute(s->cursor, &e->pointer->base, e->x, e->y);
	process_motion(s, e->time_msec);
}

static bool alt_down(struct vwm_server *s)
{
	struct wlr_keyboard *kb = wlr_seat_get_keyboard(s->seat);
	return kb && (wlr_keyboard_get_modifiers(kb) & WLR_MODIFIER_ALT);
}

static void on_cursor_button(struct wl_listener *l, void *data)
{
	struct vwm_server *s = FROM(struct vwm_server, l, cursor_button);
	struct wlr_pointer_button_event *e = data;
	if (e->state == WL_POINTER_BUTTON_STATE_RELEASED) {
		if (s->sticky_grab)
			return;   /* Move or Size from the window menu: it ends with the next click */
		bool was_grab = s->mode != CURSOR_PASSTHROUGH;
		s->mode = CURSOR_PASSTHROUGH;
		s->grabbed = NULL;
		if (s->pressed_view) {   /* a chrome button: it fires when released over the same button */
			struct vwm_view *v = s->pressed_view;
			enum chrome_button b = s->pressed_button, over = BTN_NONE;
			double nx, ny;
			struct chrome_hit *hit = chrome_under_cursor(s, &nx, &ny);
			if (hit && hit->view == v && hit->kind == HIT_TITLE)
				over = button_at(v, nx);
			s->pressed_view = NULL;
			v->pressed = false;
			chrome_request(v);
			if (over == b) {
				if (b == BTN_CLOSE) vwm_view_close(v);
				else if (b == BTN_MIN) vwm_view_set_minimized(v, true);
				else if (b == BTN_MAX) vwm_view_set_maximized(v, !v->maximized);
			}
			return;
		}
		if (!was_grab)
			wlr_seat_pointer_notify_button(s->seat, e->time_msec, e->button, e->state);
		return;
	}
	if (s->sticky_grab) {   /* the click that drops a window being moved or sized from the menu */
		s->sticky_grab = false;
		s->mode = CURSOR_PASSTHROUGH;
		s->grabbed = NULL;
		return;
	}
	if (s->menu_view) {
		double mx, my;
		struct chrome_hit *over = chrome_under_cursor(s, &mx, &my);
		if (over && over->kind == HIT_MENU && e->button == 0x110)
			menu_invoke(s, menu_row_at(my));
		else if (!(over && over->kind == HIT_MENU))
			menu_close(s);   /* a click anywhere else only closes the menu */
		return;
	}
	{
		double nx, ny;
		struct chrome_hit *hit = chrome_under_cursor(s, &nx, &ny);
		if (hit && hit->kind == HIT_TITLE && e->button == 0x111) {   /* right-click on the titlebar: the window menu */
			vwm_view_activate(hit->view);
			menu_open(hit->view, (int)s->cursor->x, (int)s->cursor->y);
			return;
		}
		if (hit && hit->kind == HIT_TITLE && e->button == 0x110 && nx < CHROME_ICON_W) {   /* the icon */
			struct vwm_view *v = hit->view;
			vwm_view_activate(v);
			if (s->last_click_view == v && e->time_msec - s->last_click_time < 400) {
				s->last_click_view = NULL;
				vwm_view_close(v);   /* double-click the icon closes, as it always has */
			} else {
				s->last_click_view = v;
				s->last_click_time = e->time_msec;
				menu_open(v, v->x, v->y);
			}
			return;
		}
		if (hit && e->button == 0x110) {   /* left button on a titlebar or grip */
			struct vwm_view *v = hit->view;
			vwm_view_activate(v);
			if (hit->kind == HIT_GRIP) {
				begin_grab(v, CURSOR_RESIZE, hit->edges);
				return;
			}
			uint32_t edges = title_edges(v, nx, ny);
			enum chrome_button b = button_at(v, nx);
			if (edges) {
				begin_grab(v, CURSOR_RESIZE, edges);
			} else if (b != BTN_NONE) {
				s->pressed_view = v;
				s->pressed_button = b;
				v->pressed = true;
				chrome_request(v);
			} else if (s->last_click_view == v && e->time_msec - s->last_click_time < 400) {
				s->last_click_view = NULL;   /* double-click the titlebar: whatever the shell configured */
				switch (s->doubleclick) {
				case DBL_MAXIMIZE: vwm_view_set_maximized(v, !v->maximized); break;
				case DBL_MINIMIZE: vwm_view_set_minimized(v, true); break;
				case DBL_CLOSE: vwm_view_close(v); break;
				case DBL_NONE: break;
				}
			} else {
				s->last_click_view = v;
				s->last_click_time = e->time_msec;
				if (!v->maximized)
					begin_grab(v, CURSOR_MOVE, 0);
			}
			return;
		}
		if (hit)
			return;   /* other buttons on chrome are not passed to the window below */
	}
	double sx, sy;
	struct wlr_surface *surface = NULL;
	struct vwm_view *v = view_at(s, s->cursor->x, s->cursor->y, &surface, &sx, &sy);
	if (v && !(v->x11 && v->x11->override_redirect))
		vwm_view_activate(v);
	if (v && alt_down(s)) {   /* Alt + left drag moves, Alt + right drag resizes */
		if (e->button == 0x110) { begin_grab(v, CURSOR_MOVE, 0); return; }
		if (e->button == 0x111) {
			uint32_t edges = 0;
			struct wlr_box g;
			view_geometry(v, &g);
			edges |= (s->cursor->x < v->x + g.width / 2) ? WLR_EDGE_LEFT : WLR_EDGE_RIGHT;
			edges |= (s->cursor->y < v->y + g.height / 2) ? WLR_EDGE_TOP : WLR_EDGE_BOTTOM;
			begin_grab(v, CURSOR_RESIZE, edges);
			return;
		}
	}
	wlr_seat_pointer_notify_button(s->seat, e->time_msec, e->button, e->state);
}

static void on_cursor_axis(struct wl_listener *l, void *data)
{
	struct vwm_server *s = FROM(struct vwm_server, l, cursor_axis);
	struct wlr_pointer_axis_event *e = data;
	wlr_seat_pointer_notify_axis(s->seat, e->time_msec, e->orientation, e->delta, e->delta_discrete, e->source, e->relative_direction);
}

static void on_cursor_frame(struct wl_listener *l, void *data)
{
	wlr_seat_pointer_notify_frame(FROM(struct vwm_server, l, cursor_frame)->seat);
}

static void on_request_cursor(struct wl_listener *l, void *data)
{
	struct vwm_server *s = FROM(struct vwm_server, l, request_cursor);
	struct wlr_seat_pointer_request_set_cursor_event *e = data;
	if (s->seat->pointer_state.focused_client == e->seat_client)
		wlr_cursor_set_surface(s->cursor, e->surface, e->hotspot_x, e->hotspot_y);
}

static void on_request_set_selection(struct wl_listener *l, void *data)
{
	struct vwm_server *s = FROM(struct vwm_server, l, request_set_selection);
	struct wlr_seat_request_set_selection_event *e = data;
	wlr_seat_set_selection(s->seat, e->source, e->serial);
}

static void on_request_set_primary(struct wl_listener *l, void *data)
{
	struct vwm_server *s = FROM(struct vwm_server, l, request_set_primary);
	struct wlr_seat_request_set_primary_selection_event *e = data;
	wlr_seat_set_primary_selection(s->seat, e->source, e->serial);
}

/* Compositor shortcuts. Returns true when the key was ours. */
static bool handle_shortcut(struct vwm_server *s, xkb_keysym_t sym, uint32_t mods)
{
	bool alt = mods & WLR_MODIFIER_ALT, ctrl = mods & WLR_MODIFIER_CTRL, logo = mods & WLR_MODIFIER_LOGO;
	if (ctrl && alt && sym == XKB_KEY_BackSpace) { wl_display_terminate(s->display); return true; }
	if (ctrl && alt && sym >= XKB_KEY_XF86Switch_VT_1 && sym <= XKB_KEY_XF86Switch_VT_12) {
		if (s->session)
			wlr_session_change_vt(s->session, sym - XKB_KEY_XF86Switch_VT_1 + 1);
		return true;
	}
	if (s->menu_view && sym == XKB_KEY_Escape) {
		menu_close(s);
		return true;
	}
	if (alt && sym == XKB_KEY_space) {
		if (s->focused && s->focused->decorated)
			menu_open(s->focused, s->focused->x, s->focused->y);
		return true;
	}
	if (alt && sym == XKB_KEY_F4) {
		if (s->focused)
			vwm_view_close(s->focused);
		return true;
	}
	if (alt && sym == XKB_KEY_Tab) {   /* next window: the one used least recently comes forward */
		struct vwm_view *last = NULL, *v;
		wl_list_for_each(v, &s->views, link)
			if (v->mapped && !v->minimized)
				last = v;
		if (last && last != s->focused)
			focus_view(last);
		return true;
	}
	if (logo && (sym == XKB_KEY_space || sym == XKB_KEY_Super_L)) {
		spawn(s->launcher);
		return true;
	}
	if (logo && sym == XKB_KEY_Up) {
		if (s->focused) vwm_view_set_maximized(s->focused, true);
		return true;
	}
	if (logo && sym == XKB_KEY_Down) {
		if (s->focused) {
			if (s->focused->maximized) vwm_view_set_maximized(s->focused, false);
			else vwm_view_set_minimized(s->focused, true);
		}
		return true;
	}
	return false;
}

static void keyboard_key(struct wl_listener *l, void *data)
{
	struct vwm_keyboard *k = FROM(struct vwm_keyboard, l, key);
	struct vwm_server *s = k->server;
	struct wlr_keyboard_key_event *e = data;
	uint32_t keycode = e->keycode + 8;
	const xkb_keysym_t *syms;
	int n = xkb_state_key_get_syms(k->keyboard->xkb_state, keycode, &syms);
	bool handled = false;
	if (e->state == WL_KEYBOARD_KEY_STATE_PRESSED) {
		uint32_t mods = wlr_keyboard_get_modifiers(k->keyboard);
		for (int i = 0; i < n; i++)
			handled |= handle_shortcut(s, syms[i], mods);
	}
	if (!handled) {
		wlr_seat_set_keyboard(s->seat, k->keyboard);
		wlr_seat_keyboard_notify_key(s->seat, e->time_msec, e->keycode, e->state);
	}
}

static void keyboard_modifiers(struct wl_listener *l, void *data)
{
	struct vwm_keyboard *k = FROM(struct vwm_keyboard, l, modifiers);
	wlr_seat_set_keyboard(k->server->seat, k->keyboard);
	wlr_seat_keyboard_notify_modifiers(k->server->seat, &k->keyboard->modifiers);
}

static void keyboard_destroy(struct wl_listener *l, void *data)
{
	struct vwm_keyboard *k = FROM(struct vwm_keyboard, l, destroy);
	wl_list_remove(&k->modifiers.link);
	wl_list_remove(&k->key.link);
	wl_list_remove(&k->destroy.link);
	wl_list_remove(&k->link);
	free(k);
}

static void add_keyboard(struct vwm_server *s, struct wlr_input_device *device)
{
	struct wlr_keyboard *kb = wlr_keyboard_from_input_device(device);
	struct vwm_keyboard *k = calloc(1, sizeof *k);
	k->server = s;
	k->keyboard = kb;
	struct xkb_context *ctx = xkb_context_new(XKB_CONTEXT_NO_FLAGS);
	struct xkb_keymap *map = xkb_keymap_new_from_names(ctx, NULL, XKB_KEYMAP_COMPILE_NO_FLAGS);   /* XKB_DEFAULT_* from the environment */
	wlr_keyboard_set_keymap(kb, map);
	xkb_keymap_unref(map);
	xkb_context_unref(ctx);
	wlr_keyboard_set_repeat_info(kb, 25, 600);
	k->modifiers.notify = keyboard_modifiers; wl_signal_add(&kb->events.modifiers, &k->modifiers);
	k->key.notify = keyboard_key;             wl_signal_add(&kb->events.key, &k->key);
	k->destroy.notify = keyboard_destroy;     wl_signal_add(&device->events.destroy, &k->destroy);
	wlr_seat_set_keyboard(s->seat, kb);
	wl_list_insert(&s->keyboards, &k->link);
}

static void on_new_input(struct wl_listener *l, void *data)
{
	struct vwm_server *s = FROM(struct vwm_server, l, new_input);
	struct wlr_input_device *device = data;
	uint32_t caps = WL_SEAT_CAPABILITY_POINTER;
	switch (device->type) {
	case WLR_INPUT_DEVICE_KEYBOARD: add_keyboard(s, device); break;
	case WLR_INPUT_DEVICE_POINTER: wlr_cursor_attach_input_device(s->cursor, device); break;
	default: break;
	}
	caps |= WL_SEAT_CAPABILITY_KEYBOARD;   /* the virtual keyboard keeps the capability when no real one is attached */
	wlr_seat_set_capabilities(s->seat, caps);
}

/* ---------- start-up ---------- */

static void usage(const char *argv0)
{
	fprintf(stderr,
		"usage: %s [-s command] [-l launcher] [-i socket] [-d] [-v]\n"
		"  -s  program to start once the compositor is up (the shell)\n"
		"  -l  command for Super+Space (default: vexplore --launcher)\n"
		"  -i  control socket (default: $XDG_RUNTIME_DIR/vde/vwm.sock)\n"
		"  -d  no server-side decorations: windows draw their own titlebars\n"
		"  -v  verbose log\n", argv0);
}

int main(int argc, char **argv)
{
	const char *startup = NULL, *socket_path = NULL, *launcher = "vexplore --launcher";
	enum wlr_log_importance level = WLR_INFO;
	bool no_chrome = false;
	int c;
	while ((c = getopt(argc, argv, "s:l:i:dvh")) != -1) {
		switch (c) {
		case 's': startup = optarg; break;
		case 'l': launcher = optarg; break;
		case 'i': socket_path = optarg; break;
		case 'd': no_chrome = true; break;
		case 'v': level = WLR_DEBUG; break;
		default: usage(argv[0]); return c == 'h' ? 0 : 1;
		}
	}
	wlr_log_init(level, NULL);

	struct vwm_server s = { 0 };
	the_server = &s;
	s.launcher = launcher;
	s.no_chrome = no_chrome;
	wl_list_init(&s.outputs);
	wl_list_init(&s.keyboards);
	wl_list_init(&s.views);
	wl_list_init(&s.layer_surfaces);

	s.display = wl_display_create();
	s.backend = wlr_backend_autocreate(wl_display_get_event_loop(s.display), &s.session);
	if (!s.backend) {
		wlr_log(WLR_ERROR, "no backend: need a seat (DRM) or a Wayland/X11 session to run in");
		return 1;
	}
	s.renderer = wlr_renderer_autocreate(s.backend);
	if (!s.renderer) { wlr_log(WLR_ERROR, "no renderer"); return 1; }
	wlr_renderer_init_wl_display(s.renderer, s.display);
	s.allocator = wlr_allocator_autocreate(s.backend, s.renderer);
	if (!s.allocator) { wlr_log(WLR_ERROR, "no allocator"); return 1; }

	s.compositor = wlr_compositor_create(s.display, 6, s.renderer);
	wlr_subcompositor_create(s.display);
	wlr_data_device_manager_create(s.display);
	wlr_primary_selection_v1_device_manager_create(s.display);
	wlr_viewporter_create(s.display);
	wlr_fractional_scale_manager_v1_create(s.display, 1);

	s.output_layout = wlr_output_layout_create(s.display);
	wlr_xdg_output_manager_v1_create(s.display, s.output_layout);
	s.scene = wlr_scene_create();
	s.scene_layout = wlr_scene_attach_output_layout(s.scene, s.output_layout);
	float dark[4] = { 0.07f, 0.09f, 0.14f, 1.0f };
	s.background = wlr_scene_rect_create(&s.scene->tree, 16384, 16384, dark);
	wlr_scene_node_set_position(&s.background->node, -4096, -4096);
	for (int i = 0; i < LAYER_COUNT; i++)
		s.layers[i] = wlr_scene_tree_create(&s.scene->tree);

	s.new_output.notify = on_new_output;
	wl_signal_add(&s.backend->events.new_output, &s.new_output);

	s.xdg_shell = wlr_xdg_shell_create(s.display, 6);
	s.new_xdg_toplevel.notify = on_new_xdg_toplevel;
	wl_signal_add(&s.xdg_shell->events.new_toplevel, &s.new_xdg_toplevel);
	static struct wl_listener popup_listener;
	popup_listener.notify = on_new_xdg_popup;
	wl_signal_add(&s.xdg_shell->events.new_popup, &popup_listener);
	struct wlr_xdg_decoration_manager_v1 *deco = wlr_xdg_decoration_manager_v1_create(s.display);
	s.new_decoration.notify = on_new_decoration;
	wl_signal_add(&deco->events.new_toplevel_decoration, &s.new_decoration);

	s.layer_shell = wlr_layer_shell_v1_create(s.display, 4);
	s.new_layer.notify = on_new_layer;
	wl_signal_add(&s.layer_shell->events.new_surface, &s.new_layer);

	s.toplevels = wlr_foreign_toplevel_manager_v1_create(s.display);

	s.cursor = wlr_cursor_create();
	wlr_cursor_attach_output_layout(s.cursor, s.output_layout);
	s.xcursor = wlr_xcursor_manager_create(NULL, 24);
	s.cursor_motion.notify = on_cursor_motion;                   wl_signal_add(&s.cursor->events.motion, &s.cursor_motion);
	s.cursor_motion_absolute.notify = on_cursor_motion_absolute; wl_signal_add(&s.cursor->events.motion_absolute, &s.cursor_motion_absolute);
	s.cursor_button.notify = on_cursor_button;                   wl_signal_add(&s.cursor->events.button, &s.cursor_button);
	s.cursor_axis.notify = on_cursor_axis;                       wl_signal_add(&s.cursor->events.axis, &s.cursor_axis);
	s.cursor_frame.notify = on_cursor_frame;                     wl_signal_add(&s.cursor->events.frame, &s.cursor_frame);

	s.seat = wlr_seat_create(s.display, "seat0");
	s.new_input.notify = on_new_input;                 wl_signal_add(&s.backend->events.new_input, &s.new_input);
	s.request_cursor.notify = on_request_cursor;       wl_signal_add(&s.seat->events.request_set_cursor, &s.request_cursor);
	s.request_set_selection.notify = on_request_set_selection; wl_signal_add(&s.seat->events.request_set_selection, &s.request_set_selection);
	s.request_set_primary.notify = on_request_set_primary;     wl_signal_add(&s.seat->events.request_set_primary_selection, &s.request_set_primary);

	s.virtual_keyboard = wlr_keyboard_group_create();
	{
		struct xkb_context *ctx = xkb_context_new(XKB_CONTEXT_NO_FLAGS);
		struct xkb_rule_names names = { .rules = "evdev", .model = "pc105", .layout = getenv("XKB_DEFAULT_LAYOUT") ? getenv("XKB_DEFAULT_LAYOUT") : "us", .variant = getenv("XKB_DEFAULT_VARIANT"), .options = getenv("XKB_DEFAULT_OPTIONS") };
		struct xkb_keymap *map = xkb_keymap_new_from_names(ctx, &names, XKB_KEYMAP_COMPILE_NO_FLAGS);
		if (getenv("VWM_DUMP_KEYMAP")) {
			char *str = xkb_keymap_get_as_string(map, XKB_KEYMAP_FORMAT_TEXT_V1);
			FILE *f = fopen(getenv("VWM_DUMP_KEYMAP"), "w");
			if (f) { fputs(str, f); fclose(f); }
			free(str);
		}
		wlr_keyboard_set_keymap(&s.virtual_keyboard->keyboard, map);
		xkb_keymap_unref(map);
		xkb_context_unref(ctx);
	}
	wlr_seat_set_keyboard(s.seat, &s.virtual_keyboard->keyboard);
	wlr_seat_set_capabilities(s.seat, WL_SEAT_CAPABILITY_POINTER | WL_SEAT_CAPABILITY_KEYBOARD);

	s.xwayland = wlr_xwayland_create(s.display, s.compositor, false);
	if (s.xwayland) {
		s.new_x11.notify = on_new_x11;   wl_signal_add(&s.xwayland->events.new_surface, &s.new_x11);
		s.x11_ready.notify = on_x11_ready; wl_signal_add(&s.xwayland->events.ready, &s.x11_ready);
	} else {
		wlr_log(WLR_ERROR, "XWayland did not start: X11 programs will not run");
	}

	const char *socket = wl_display_add_socket_auto(s.display);
	if (!socket) {
		wlr_log(WLR_ERROR, "cannot create the Wayland socket");
		wlr_backend_destroy(s.backend);
		return 1;
	}

	char ipc_path[108];
	if (!socket_path) {
		const char *rt = getenv("XDG_RUNTIME_DIR");
		snprintf(ipc_path, sizeof ipc_path, "%s/vde/vwm.sock", rt ? rt : "/tmp");
		socket_path = ipc_path;
	}
	s.ipc = vwm_ipc_create(&s, socket_path);
	if (!s.ipc)
		wlr_log(WLR_ERROR, "cannot create the control socket %s: the taskbar will not see windows", socket_path);

	if (!wlr_backend_start(s.backend)) {
		wlr_log(WLR_ERROR, "cannot start the backend");
		wlr_backend_destroy(s.backend);
		wl_display_destroy(s.display);
		return 1;
	}

	setenv("WAYLAND_DISPLAY", socket, true);
	if (s.xwayland)
		setenv("DISPLAY", s.xwayland->display_name, true);
	wlr_log(WLR_INFO, "vwm running on WAYLAND_DISPLAY=%s, control socket %s", socket, socket_path);
	if (startup)
		spawn(startup);

	wl_display_run(s.display);

	wl_display_destroy_clients(s.display);
	vwm_ipc_destroy(s.ipc);
	wl_list_remove(&s.new_input.link);
	wlr_xcursor_manager_destroy(s.xcursor);
	wlr_cursor_destroy(s.cursor);
	if (s.xwayland)
		wlr_xwayland_destroy(s.xwayland);
	wlr_scene_node_destroy(&s.scene->tree.node);
	wlr_allocator_destroy(s.allocator);
	wlr_renderer_destroy(s.renderer);
	wlr_backend_destroy(s.backend);
	wl_display_destroy(s.display);
	return 0;
}
