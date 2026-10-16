/* vterm-launch: the child side of a terminal session.
 *
 *   vterm-launch SLAVE [-C DIR] -- PROGRAM [ARGS...]
 *
 * The terminal emulator opens a pseudo-terminal and starts this helper (a .NET process must not fork), which makes
 * itself a session leader with the pty slave as its controlling terminal on stdin/stdout/stderr, goes to DIR, and
 * becomes PROGRAM. Everything is plain POSIX; nothing here is specific to VDE. */
#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <unistd.h>

int main(int argc, char **argv)
{
	if (argc < 4) {
		fprintf(stderr, "usage: vterm-launch SLAVE [-C DIR] -- PROGRAM [ARGS...]\n");
		return 2;
	}
	const char *slave = argv[1], *dir = NULL;
	int i = 2;
	if (i + 1 < argc && !strcmp(argv[i], "-C")) {
		dir = argv[i + 1];
		i += 2;
	}
	if (i >= argc || strcmp(argv[i], "--") || i + 1 >= argc) {
		fprintf(stderr, "vterm-launch: missing -- PROGRAM\n");
		return 2;
	}
	i++;

	setsid();
	int fd = open(slave, O_RDWR);
	if (fd < 0) {
		perror("vterm-launch: open slave");
		return 1;
	}
	ioctl(fd, TIOCSCTTY, 0);
	dup2(fd, 0);
	dup2(fd, 1);
	dup2(fd, 2);
	if (fd > 2)
		close(fd);
	/* a clean slate: the terminal's own program may have ignored or blocked signals */
	for (int s = 1; s < 32; s++)
		if (s != SIGKILL && s != SIGSTOP)
			signal(s, SIG_DFL);
	if (dir && chdir(dir) != 0)
		chdir(getenv("HOME") ? getenv("HOME") : "/");
	execvp(argv[i], argv + i);
	fprintf(stderr, "vterm-launch: cannot run %s: %s\n", argv[i], strerror(errno));
	return 127;
}
