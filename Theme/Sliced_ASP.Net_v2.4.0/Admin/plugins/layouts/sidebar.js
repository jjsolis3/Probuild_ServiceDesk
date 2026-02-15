const plugin = require('tailwindcss/plugin');

module.exports = plugin(function ({ addComponents }) {
    addComponents({
        '.sidebar': {
            '@apply fixed z-[9999] lg:z-[10] flex-none w-[240px] ltr:border-r rtl:border-l dark:bg-darkborder border-black/10 transition-all duration-300 overflow-hidden transition-all duration-300': {},
            //dark sidebar
            '@apply group-data-[sidebar=dark]/item:bg-darkborder group-data-[sidebar=dark]/item:border-darkborder group-data-[sidebar=brand]/item:border-sky-900': {},

            '&:hover': {
                '.nav-item': {
                    '> a': {
                        '@apply w-auto': {},
                    }
                }
            },

            '.nav-item': {
                '> a': {
                    '@apply flex items-center gap-1 py-1 mb-1 overflow-hidden text-black rounded-md whitespace-nowrap dark:text-white hover:text-primary last:mb-0 group-data-[sidebar=dark]/item:text-white group-data-[sidebar=brand]/item:text-sky-200/50': {},
                    //active
                    '@apply [&.active]:text-primary group-data-[sidebar=dark]/item:[&.active]:text-primary group-data-[sidebar=brand]/item:[&.active]:text-sky-500': {},
                }
            },

            'ul.sub-menu': {
                'li': {
                    'a': {
                        '@apply flex items-center capitalize relative ltr:pl-7 rtl:pr-7 px-4 py-1 before:transition-all before:duration-300 rounded-lg hover:text-primary group-data-[sidebar=brand]/item:hover:text-sky-500 hover:before:h-1.5 hover:before:w-1.5 hover:before:bg-primary group-data-[sidebar=brand]/item:hover:before:bg-sky-500 hover:before:absolute hover:before:top-1/2 hover:before:-translate-y-1/2 ltr:hover:before:left-2 rtl:hover:before:right-2 hover:before:rounded-full': {},
                        //active
                        '@apply [&.active]:text-primary group-data-[sidebar=brand]/item:[&.active]:text-sky-500 [&.active]:before:h-1.5 [&.active]:before:w-1.5 [&.active]:before:bg-primary group-data-[sidebar=brand]/item:[&.active]:before:bg-sky-500 [&.active]:before:absolute [&.active]:before:top-1/2 [&.active]:before:-translate-y-1/2 [&.active]:ltr:before:left-2 [&.active]:rtl:before:right-2 [&.active]:before:rounded-full': {},
                    }
                }
            },
        },
    });
});
