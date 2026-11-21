#ifndef VWM_H
#define VWM_H

#include <stdbool.h>
#include <stdint.h>
#include <wayland-server-core.h>
#include <wlr/types/wlr_scene.h>
#include <wlr/util/box.h>

struct vwm_server;
struct vwm_view;

/* ipc.c: a Unix socket the shell (C#) uses to see the windows and to act on them. One JSON object per line out,
 * one command per line in. See docs/vwm.md. */
struct vwm_ipc;
struct vwm_ipc *vwm_ipc_create(struct vwm_server *server, const char *path);
void vwm_ipc_destroy(struct vwm_ipc *ipc);
void vwm_ipc_window(struct vwm_ipc *ipc, struct vwm_view *view);   /* opened or changed */
void vwm_ipc_closed(struct vwm_ipc *ipc, uint32_t id);
bool vwm_ipc_start(struct vwm_ipc *ipc);   /* the Start key: true when a shell took it */
void vwm_ipc_menu_request(struct vwm_ipc *ipc, struct vwm_view *view, unsigned enabled, int hover);
void vwm_ipc_chrome_request(struct vwm_ipc *ipc, struct vwm_view *view);   /* the titlebar needs (re)drawing */

/* What the shell may know about a window, and what it may ask of it (implemented in vwm.c). */
/* Window chrome geometry (logical pixels). The shell draws a titlebar image of exactly (content width x TITLEBAR_H);
 * vwm positions it and does the hit-testing, so the buttons must be where the shell draws them: three buttons of
 * CHROME_BUTTON_W at the right end, in the order minimize, maximize, close. */
#define TITLEBAR_H 32
#define CHROME_BUTTON_W 46
#define CHROME_GRIP 6
#define CHROME_RESIZE_TOP 4
#define CHROME_ICON_W 30   /* a click in the first CHROME_ICON_W pixels of the titlebar opens the window menu */

/* The window menu: the shell draws one MENU_W x MENU_HEIGHT image; rows are MENU_ROW_H high inside MENU_PAD.
 * Order: Restore, Move, Size, Minimize, Maximize, Close. */
#define MENU_W 220
#define MENU_ROWS 6
#define MENU_ROW_H 30
#define MENU_PAD 4
#define MENU_HEIGHT (MENU_ROWS * MENU_ROW_H + 2 * MENU_PAD)

struct vwm_view_info {
	uint32_t id;
	const char *title, *app_id;
	bool active, minimized, maximized, fullscreen, x11, decorated, pressed;
	const char *hover;   /* "none", "min", "max" or "close": the chrome button under the pointer */
	int x, y, width, height;
	unsigned pid;   /* the client's pid: xdg toplevels have neither window types nor struts, so the shell
	                 * announces the desktop/dock roles of its own windows over the control socket, and
	                 * identifies itself by this (a process never shares a window with another) */
};
void vwm_view_get_info(struct vwm_view *view, struct vwm_view_info *out);
/* Claims a role for a window over the control socket: "desktop" (the wallpaper window, full screen),
 * "top"/"bottom"/"left"/"right" (a dock: it reserves its edge), "none" (back to a normal window). */
void vwm_view_set_role(struct vwm_server *server, uint32_t id, const char *spec);
void vwm_taskview(struct vwm_server *server);   /* the taskbar's Task view button */
void vwm_view_set_popup(struct vwm_server *server, uint32_t id, int x, int y);   /* a flyout (the Start menu): no frame, not listed, placed at x,y, focused */
struct vwm_view *vwm_find_view(struct vwm_server *server, uint32_t id);
void vwm_each_view(struct vwm_server *server, void (*fn)(struct vwm_view *, void *), void *data);
void vwm_view_activate(struct vwm_view *view);
void vwm_view_set_minimized(struct vwm_view *view, bool minimized);
void vwm_view_set_maximized(struct vwm_view *view, bool maximized);
void vwm_view_close(struct vwm_view *view);
/* Asks the shell to draw the titlebar again (a shell that connects late has missed the requests). */
void vwm_view_redraw_chrome(struct vwm_view *view);
/* The shell's drawing of the titlebar; takes ownership of `bgra` (premultiplied ARGB8888, width*height*4 bytes). */
void vwm_view_set_chrome(struct vwm_view *view, int width, int height, void *bgra);
/* The shell's drawing of the window menu for `view` (same format; MENU_W x MENU_HEIGHT). */
/* Opens the window menu at the window's top-left corner. */
void vwm_view_open_menu(struct vwm_view *view);
void vwm_view_set_menu(struct vwm_view *view, int width, int height, void *bgra);
void vwm_set_border_colors(struct vwm_server *server, uint32_t focused, uint32_t other);   /* 0xRRGGBB each */
void vwm_set_titlebar_doubleclick(struct vwm_server *server, const char *action);   /* maximize, minimize, close or none */
struct wl_event_loop *vwm_event_loop(struct vwm_server *server);
void vwm_quit(struct vwm_server *server);

/* json.c */
size_t vwm_json_escape(char *out, size_t cap, const char *in);

#endif
