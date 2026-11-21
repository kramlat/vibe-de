/* The shell's view of the compositor: window events out, window commands in.
 *
 * Socket: $XDG_RUNTIME_DIR/vde/vwm.sock (mode 0600; only the user who runs the session may connect).
 * Out (one JSON object per line):
 *   {"event":"window","id":3,"title":"...","app_id":"...","active":true,"minimized":false,"maximized":false,
 *    "fullscreen":false,"x11":false,"x":10,"y":20,"width":800,"height":600}
 *   {"event":"closed","id":3}
 * In (one command per line):  list | role N desktop|top|bottom|left|right|none | activate N | minimize N | restore N | maximize N | unmaximize N | close N | quit
 *   {"event":"chrome","id":3,"width":800,"active":true,"maximized":false,"hover":"close","pressed":false,
 *    "title":"...","app_id":"..."}      <- the titlebar of window 3 needs drawing
 *   chrome N W H\n + W*H*4 bytes        -> its image, premultiplied ARGB8888 (BGRA bytes), W x H = width x TITLEBAR_H
 *   {"event":"menu","id":3,"enabled":63,"hover":2,"maximized":false}   <- the window menu of 3 is open and needs drawing

 *   titlebar-doubleclick maximize|minimize|close|none     what double-clicking a titlebar does (default maximize)
 *   menu N                                                open the window menu of N at its top-left corner
 *   menu N W H\n + W*H*4 bytes          -> its image (MENU_W x MENU_HEIGHT); enabled is a bit per row, in row order
 */
#define _GNU_SOURCE
#include "vwm.h"

#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/un.h>
#include <unistd.h>

#define MAX_CLIENTS 8

struct client {
	int fd;
	struct wl_event_source *source;
	struct vwm_ipc *ipc;
	char buf[256];
	size_t len;
	bool wants_start;   /* the shell that opens and closes the Start menu ("handle-start") */
	/* a titlebar image being received */
	uint32_t chrome_id;
	bool payload_is_menu;
	int chrome_w, chrome_h;
	uint8_t *payload;
	size_t payload_len, payload_got;
};

#define MAX_CHROME_BYTES (8192u * 64u * 4u)

struct vwm_ipc {
	struct vwm_server *server;
	int listen_fd;
	struct wl_event_source *listen_source;
	char path[108];
	struct client *clients[MAX_CLIENTS];
};

static void drop_client(struct client *c)
{
	for (int i = 0; i < MAX_CLIENTS; i++)
		if (c->ipc->clients[i] == c)
			c->ipc->clients[i] = NULL;
	wl_event_source_remove(c->source);
	close(c->fd);
	free(c->payload);
	free(c);
}

static void send_line(struct client *c, const char *line)
{
	/* A shell that stops reading is dropped rather than allowed to stall the compositor. */
	size_t n = strlen(line);
	if (send(c->fd, line, n, MSG_DONTWAIT | MSG_NOSIGNAL) != (ssize_t)n)
		drop_client(c);
}

static void broadcast(struct vwm_ipc *ipc, const char *line)
{
	for (int i = 0; i < MAX_CLIENTS; i++)
		if (ipc->clients[i])
			send_line(ipc->clients[i], line);
}

static size_t format_window(struct vwm_view *view, char *out, size_t cap)
{
	struct vwm_view_info i;
	vwm_view_get_info(view, &i);
	char title[512], app[256];
	vwm_json_escape(title, sizeof title, i.title);
	vwm_json_escape(app, sizeof app, i.app_id);
	int n = snprintf(out, cap,
		"{\"event\":\"window\",\"id\":%u,\"title\":\"%s\",\"app_id\":\"%s\",\"active\":%s,\"minimized\":%s,"
		"\"maximized\":%s,\"fullscreen\":%s,\"x11\":%s,\"pid\":%u,\"x\":%d,\"y\":%d,\"width\":%d,\"height\":%d}\n",
		i.id, title, app, i.active ? "true" : "false", i.minimized ? "true" : "false",
		i.maximized ? "true" : "false", i.fullscreen ? "true" : "false", i.x11 ? "true" : "false", i.pid,
		i.x, i.y, i.width, i.height);
	return n < 0 ? 0 : (size_t)n;
}

void vwm_ipc_window(struct vwm_ipc *ipc, struct vwm_view *view)
{
	char line[1400];
	if (format_window(view, line, sizeof line))
		broadcast(ipc, line);
}

void vwm_ipc_chrome_request(struct vwm_ipc *ipc, struct vwm_view *view)
{
	struct vwm_view_info i;
	vwm_view_get_info(view, &i);
	char title[512], app[256], line[1100];
	vwm_json_escape(title, sizeof title, i.title);
	vwm_json_escape(app, sizeof app, i.app_id);
	snprintf(line, sizeof line,
		"{\"event\":\"chrome\",\"id\":%u,\"width\":%d,\"active\":%s,\"maximized\":%s,\"hover\":\"%s\","
		"\"pressed\":%s,\"title\":\"%s\",\"app_id\":\"%s\"}\n",
		i.id, i.width, i.active ? "true" : "false", i.maximized ? "true" : "false", i.hover,
		i.pressed ? "true" : "false", title, app);
	broadcast(ipc, line);
}

void vwm_ipc_menu_request(struct vwm_ipc *ipc, struct vwm_view *view, unsigned enabled, int hover)
{
	struct vwm_view_info i;
	vwm_view_get_info(view, &i);
	char line[160];
	snprintf(line, sizeof line, "{\"event\":\"menu\",\"id\":%u,\"enabled\":%u,\"hover\":%d,\"maximized\":%s}\n",
		i.id, enabled, hover, i.maximized ? "true" : "false");
	broadcast(ipc, line);
}

/* The Start key was pressed alone (or Super+Space): tell the shell that handles Start. False when none does. */
bool vwm_ipc_start(struct vwm_ipc *ipc)
{
	bool sent = false;
	for (int i = 0; i < MAX_CLIENTS; i++)
		if (ipc->clients[i] && ipc->clients[i]->wants_start) {
			send_line(ipc->clients[i], "{\"event\":\"start\"}\n");
			sent = true;
		}
	return sent;
}

void vwm_ipc_closed(struct vwm_ipc *ipc, uint32_t id)
{
	char line[64];
	snprintf(line, sizeof line, "{\"event\":\"closed\",\"id\":%u}\n", id);
	broadcast(ipc, line);
}

static void list_one(struct vwm_view *view, void *data)
{
	struct client *c = data;
	char line[1400];
	if (c && format_window(view, line, sizeof line))
		send_line(c, line);
}

static void run_command(struct client *c, char *line)
{
	char verb[16] = "";
	unsigned id = 0;
	int got = sscanf(line, "%15s %u", verb, &id);
	if (got < 1)
		return;
	if (!strcmp(verb, "list")) {
		vwm_each_view(c->ipc->server, list_one, c);
		return;
	}
	if (!strcmp(verb, "role")) {
		char spec[16] = "";
		if (sscanf(line, "role %u %15s", &id, spec) == 2)
			vwm_view_set_role(c->ipc->server, id, spec);
		return;
	}
	if (!strcmp(verb, "chrome") || !strcmp(verb, "menu")) {
		unsigned w = 0, h = 0;
		bool menu = verb[0] == 'm';
		if (menu && sscanf(line, "menu %u %u %u", &id, &w, &h) < 3) {   /* just "menu N": open it (a taskbar's window menu) */
			struct vwm_view *view = vwm_find_view(c->ipc->server, id);
			if (view)
				vwm_view_open_menu(view);
			return;
		}
		if (sscanf(line, menu ? "menu %u %u %u" : "chrome %u %u %u", &id, &w, &h) == 3 && w > 0 && h > 0 && w <= 8192 && h <= 512 && (size_t)w * h * 4 <= MAX_CHROME_BYTES) {
			free(c->payload);
			c->chrome_id = id; c->chrome_w = (int)w; c->chrome_h = (int)h;
			c->payload_is_menu = menu;
			c->payload_len = (size_t)w * h * 4;
			c->payload_got = 0;
			c->payload = malloc(c->payload_len);
		}
		return;
	}
	if (!strcmp(verb, "titlebar-doubleclick")) {
		char action[16] = "";
		if (sscanf(line, "titlebar-doubleclick %15s", action) == 1)
			vwm_set_titlebar_doubleclick(c->ipc->server, action);
		return;
	}
	if (!strcmp(verb, "taskview")) {
		vwm_taskview(c->ipc->server);
		return;
	}
	if (!strcmp(verb, "handle-start")) {
		c->wants_start = true;
		return;
	}
	if (!strcmp(verb, "popup")) {
		int x = 0, y = 0;
		if (sscanf(line, "popup %u %d %d", &id, &x, &y) == 3)
			vwm_view_set_popup(c->ipc->server, id, x, y);
		return;
	}
	if (!strcmp(verb, "border")) {
		unsigned focused = 0, other = 0;
		if (sscanf(line, "border %x %x", &focused, &other) == 2)
			vwm_set_border_colors(c->ipc->server, focused & 0xffffff, other & 0xffffff);
		return;
	}
	if (!strcmp(verb, "quit")) {
		vwm_quit(c->ipc->server);
		return;
	}
	if (got < 2)
		return;
	struct vwm_view *view = vwm_find_view(c->ipc->server, id);
	if (!view)
		return;
	if (!strcmp(verb, "activate")) { vwm_view_set_minimized(view, false); vwm_view_activate(view); }
	else if (!strcmp(verb, "minimize")) vwm_view_set_minimized(view, true);
	else if (!strcmp(verb, "restore")) { vwm_view_set_minimized(view, false); vwm_view_activate(view); }
	else if (!strcmp(verb, "maximize")) vwm_view_set_maximized(view, true);
	else if (!strcmp(verb, "unmaximize")) vwm_view_set_maximized(view, false);
	else if (!strcmp(verb, "close")) vwm_view_close(view);
}

static void finish_chrome(struct client *c)
{
	struct vwm_view *view = vwm_find_view(c->ipc->server, c->chrome_id);
	if (view && c->payload_is_menu)
		vwm_view_set_menu(view, c->chrome_w, c->chrome_h, c->payload);   /* takes the buffer */
	else if (view)
		vwm_view_set_chrome(view, c->chrome_w, c->chrome_h, c->payload);   /* takes the buffer */
	else
		free(c->payload);
	c->payload = NULL;
	c->payload_len = c->payload_got = 0;
}

static int on_client(int fd, uint32_t mask, void *data)
{
	struct client *c = data;
	if (mask & (WL_EVENT_HANGUP | WL_EVENT_ERROR)) {
		drop_client(c);
		return 0;
	}
	uint8_t chunk[16384];
	ssize_t n = read(fd, chunk, sizeof chunk);
	if (n <= 0) {
		if (n < 0 && (errno == EAGAIN || errno == EINTR))
			return 0;
		drop_client(c);
		return 0;
	}
	for (ssize_t i = 0; i < n;) {
		if (c->payload) {   /* image bytes follow the command line */
			size_t take = c->payload_len - c->payload_got;
			if (take > (size_t)(n - i))
				take = (size_t)(n - i);
			memcpy(c->payload + c->payload_got, chunk + i, take);
			c->payload_got += take;
			i += (ssize_t)take;
			if (c->payload_got == c->payload_len)
				finish_chrome(c);
			continue;
		}
		char ch = (char)chunk[i++];
		if (ch != '\n') {
			if (c->len < sizeof c->buf - 1)
				c->buf[c->len++] = ch;
			else
				c->len = 0;   /* a line this long is not ours: start over */
			continue;
		}
		c->buf[c->len] = '\0';
		c->len = 0;
		run_command(c, c->buf);
	}
	return 0;
}

static void redraw_one(struct vwm_view *view, void *data) { vwm_view_redraw_chrome(view); }

static int on_listen(int fd, uint32_t mask, void *data)
{
	struct vwm_ipc *ipc = data;
	int cfd = accept4(fd, NULL, NULL, SOCK_CLOEXEC | SOCK_NONBLOCK);
	if (cfd < 0)
		return 0;
	int slot = -1;
	for (int i = 0; i < MAX_CLIENTS; i++)
		if (!ipc->clients[i]) { slot = i; break; }
	if (slot < 0) {
		close(cfd);
		return 0;
	}
	struct client *c = calloc(1, sizeof *c);
	c->fd = cfd;
	c->ipc = ipc;
	c->source = wl_event_loop_add_fd(vwm_event_loop(ipc->server), cfd, WL_EVENT_READABLE, on_client, c);
	ipc->clients[slot] = c;
	/* a new shell gets the current windows straight away */
	vwm_each_view(ipc->server, list_one, c);
	vwm_each_view(ipc->server, redraw_one, NULL);   /* and a chrome renderer starting late gets every titlebar to draw */
	return 0;
}

struct vwm_ipc *vwm_ipc_create(struct vwm_server *server, const char *path)
{
	struct vwm_ipc *ipc = calloc(1, sizeof *ipc);
	ipc->server = server;
	if (strlen(path) >= sizeof ipc->path) {
		free(ipc);
		return NULL;
	}
	strcpy(ipc->path, path);

	char dir[108];
	strcpy(dir, path);
	char *slash = strrchr(dir, '/');
	if (slash) {
		*slash = '\0';
		mkdir(dir, 0700);
	}
	unlink(path);
	ipc->listen_fd = socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC | SOCK_NONBLOCK, 0);
	struct sockaddr_un addr = { .sun_family = AF_UNIX };
	strcpy(addr.sun_path, path);
	mode_t old = umask(0177);
	int bound = bind(ipc->listen_fd, (struct sockaddr *)&addr, sizeof addr);
	umask(old);
	if (bound < 0 || listen(ipc->listen_fd, 4) < 0) {
		close(ipc->listen_fd);
		free(ipc);
		return NULL;
	}
	ipc->listen_source = wl_event_loop_add_fd(vwm_event_loop(server), ipc->listen_fd, WL_EVENT_READABLE, on_listen, ipc);
	return ipc;
}

void vwm_ipc_destroy(struct vwm_ipc *ipc)
{
	if (!ipc)
		return;
	for (int i = 0; i < MAX_CLIENTS; i++)
		if (ipc->clients[i])
			drop_client(ipc->clients[i]);
	wl_event_source_remove(ipc->listen_source);
	close(ipc->listen_fd);
	unlink(ipc->path);
	free(ipc);
}
