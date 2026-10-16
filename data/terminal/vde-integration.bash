# VDE terminal integration for bash (see vde-integration.ps1 for what it is for).
# vterminal starts bash with --init-file pointing here; this reads your own ~/.bashrc first.

[ -r "$HOME/.bashrc" ] && . "$HOME/.bashrc"

if [ "$VDE_TERMINAL_INTEGRATION" = 1 ] && [ -z "$__vde_integration_loaded" ]; then
    __vde_integration_loaded=1
    __vde_first=1
    __vde_prompt_command() {
        local status=$?
        if [ -z "$__vde_first" ]; then printf '\e]133;D;%s\a' "$status"; fi
        __vde_first=
        printf '\e]7;file://%s%s\a' "${HOSTNAME:-localhost}" "$(printf '%s' "$PWD" | sed -e 's/%/%25/g' -e 's/ /%20/g')"
    }
    PROMPT_COMMAND="__vde_prompt_command${PROMPT_COMMAND:+;$PROMPT_COMMAND}"
    PS1="\[\e]133;A\a\]${PS1}\[\e]133;B\a\]"
fi
