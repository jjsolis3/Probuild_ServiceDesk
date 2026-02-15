const plugin = require('tailwindcss/plugin');

module.exports = plugin(function ({ addComponents }) {
    addComponents({
        '.btn': {
            '@apply py-2.5 px-5 capitalize inline-block rounded-md transition-all duration-300 ease-linear border': {},
        },
    });
});
