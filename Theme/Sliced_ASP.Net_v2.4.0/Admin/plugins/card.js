const plugin = require('tailwindcss/plugin');

module.exports = plugin(function ({ addComponents }) {
    addComponents({
        '.card': {
            '@apply p-5 bg-white border rounded-sm border-black/10 dark:bg-darklight dark:border-darkborder': {},
        },
    });
});
