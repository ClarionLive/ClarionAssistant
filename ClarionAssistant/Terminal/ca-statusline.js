#!/usr/bin/env node
// Claude Code statusLine script for ClarionAssistant
// Reads JSON from stdin, enriches with git data, writes to temp file
// for the CA header bar to pick up.

const fs = require('fs');
const path = require('path');
const os = require('os');
const { execSync } = require('child_process');

let input = '';
process.stdin.setEncoding('utf8');
process.stdin.on('data', (chunk) => { input += chunk; });
process.stdin.on('end', () => {
    try {
        const data = JSON.parse(input);
        const tabId = process.env.CLARIONASSISTANT_TAB;

        // Graceful no-op when not running inside ClarionAssistant
        if (!tabId) process.exit(0);

        const cwd = data.workspace?.current_dir || '';
        const model = normalizeModel(data.model);
        const contextPct = data.context_window?.used_percentage ?? null;

        // Rate limits
        const rateLimits = data.rate_limits || null;
        let quota5h = null, quota7d = null, pace5h = null, pace7d = null;
        let resetIn5h = null;

        if (rateLimits) {
            if (rateLimits.five_hour) {
                quota5h = Math.floor(rateLimits.five_hour.used_percentage ?? 0);
                const resetsAt = rateLimits.five_hour.resets_at;
                if (resetsAt) {
                    const minutesRemaining = Math.max(0, Math.floor((new Date(resetsAt) - Date.now()) / 60000));
                    const windowMinutes = 300;
                    if (minutesRemaining <= windowMinutes) {
                        pace5h = Math.round(((windowMinutes - minutesRemaining) * 100 / windowMinutes) - quota5h);
                    }
                    if (minutesRemaining > 0) {
                        const h = Math.floor(minutesRemaining / 60);
                        const m = minutesRemaining % 60;
                        resetIn5h = h > 0 ? `${h}h ${m}m` : `${m}m`;
                    }
                }
            }
            if (rateLimits.seven_day) {
                quota7d = Math.floor(rateLimits.seven_day.used_percentage ?? 0);
                const resetsAt = rateLimits.seven_day.resets_at;
                if (resetsAt) {
                    const minutesRemaining = Math.max(0, Math.floor((new Date(resetsAt) - Date.now()) / 60000));
                    const windowMinutes = 10080;
                    if (minutesRemaining <= windowMinutes) {
                        pace7d = Math.round(((windowMinutes - minutesRemaining) * 100 / windowMinutes) - quota7d);
                    }
                }
            }
        }

        const isOffPeak = getIsOffPeak();
        const git = getGitInfo(cwd);

        const output = {
            tabId,
            model,
            folder: cwd,
            folderName: cwd ? path.basename(cwd) : '',
            contextPct,
            quota5h,
            quota7d,
            pace5h,
            pace7d,
            resetIn5h,
            isOffPeak,
            gitBranch: git.branch,
            gitStatus: git.status,
            gitDirty: git.dirty,
            timestamp: Date.now()
        };

        const outPath = path.join(os.tmpdir(), `ca-statusline-${tabId}.json`);
        fs.writeFileSync(outPath, JSON.stringify(output), 'utf8');
    } catch (e) {
        // Silently fail
    }
});

function normalizeModel(model) {
    if (!model) return 'claude';
    let name = typeof model === 'object' ? (model.id || model.name || 'claude') : String(model);
    name = name.replace(/^claude-/, '').replace(/-\d{8}$/, '');
    if (name.length > 16) name = name.substring(0, 16);
    return name;
}

function getIsOffPeak() {
    const now = new Date();
    const day = now.getUTCDay();
    if (day === 0 || day === 6) return true;
    const hour = now.getUTCHours();
    return hour < 12 || hour >= 18;
}

function getGitInfo(cwd) {
    if (!cwd) return { branch: '', status: '', dirty: false };
    try {
        const branch = execSync('git branch --show-current', { cwd, encoding: 'utf8', timeout: 3000 }).trim()
            || execSync('git rev-parse --short HEAD', { cwd, encoding: 'utf8', timeout: 3000 }).trim();

        const porcelain = execSync('git status --porcelain', { cwd, encoding: 'utf8', timeout: 3000 });
        const lines = porcelain.split('\n').filter(l => l.length > 0);

        let staged = 0, modified = 0;
        for (const line of lines) {
            if (/^[MADRC]/.test(line)) staged++;
            if (/^.[MD]/.test(line)) modified++;
        }

        let ahead = 0, behind = 0;
        try {
            ahead = parseInt(execSync('git rev-list --count @{u}..HEAD', { cwd, encoding: 'utf8', timeout: 3000 }).trim()) || 0;
            behind = parseInt(execSync('git rev-list --count HEAD..@{u}', { cwd, encoding: 'utf8', timeout: 3000 }).trim()) || 0;
        } catch (e) { /* no upstream */ }

        let status = '';
        if (ahead > 0) status += `\u21e1${ahead}`;
        if (behind > 0) status += `\u21e3${behind}`;
        if (staged > 0) status += `+${staged}`;
        if (modified > 0) status += `!${modified}`;

        return { branch, status, dirty: lines.length > 0 };
    } catch (e) {
        return { branch: '', status: '', dirty: false };
    }
}
