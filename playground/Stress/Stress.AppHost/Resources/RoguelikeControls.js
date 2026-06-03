// Roguelike keyboard controls — binds WASD and arrow keys to movement commands.
const keyMap = {
    'ArrowUp': 'move-up',
    'ArrowDown': 'move-down',
    'ArrowLeft': 'move-left',
    'ArrowRight': 'move-right',
    'w': 'move-up',
    'W': 'move-up',
    's': 'move-down',
    'S': 'move-down',
    'a': 'move-left',
    'A': 'move-left',
    'd': 'move-right',
    'D': 'move-right',
};

document.addEventListener('keydown', (e) => {
    const command = keyMap[e.key];
    if (!command) {
        return;
    }

    // Don't interfere when the user is typing in an input/textarea.
    if (e.target.tagName === 'INPUT' || e.target.tagName === 'TEXTAREA') {
        return;
    }

    e.preventDefault();

    const button = document.querySelector(`a[data-command="${command}"][data-resource="roguelike-commands"]`);
    if (button) {
        button.click();
    }
});
