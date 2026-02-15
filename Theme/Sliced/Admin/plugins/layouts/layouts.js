const plugin = require('tailwindcss/plugin');

module.exports = plugin(function ({ addComponents }) {
    addComponents({
        '.main-container': {
            '.main-content': {
                '@apply transition-all duration-300 lg:ms-[240px]': {},
            }
        },
        '.vertical': {
            '&.toggle-sidebar': {
                '.main-container': {
                    '.main-content': {
                        '@apply lg:ltr:ml-[72px] lg:rtl:mr-[72px]': {},
                    }
                },

                ':is(.sidebar, .sidebar:hover .help-box)': {
                    '@apply block': {},
                },
                '.sidebar': {
                    '@apply lg:w-[72px] hover:w-[240px]': {},

                    '.sub-menu': {
                        '@apply lg:hidden': {},
                    },
                    '.nav-item': {
                        'a': {
                            '@apply lg:justify-center': {},
                            'span': {
                                '@apply lg:hidden': {},
                            },
                            '.dropdown-icon': {
                                '@apply lg:hidden': {},
                            }
                        }
                    },
                    'h2, .help-box': {
                        '@apply lg:hidden': {},
                    },
                    '.main-logo .logo': {
                        '@apply lg:hidden': {},
                    },
                    '&:hover .main-logo .logo-icon': {
                        '@apply lg:hidden': {},
                    },
                    '&:hover .main-logo .logo, .main-logo .logo-icon, &:hover .sub-menu, &:hover h2, &:hover .nav-item a span': {
                        '@apply lg:block': {},
                    },
                    '&:hover': {
                        '.nav-item': {
                            'a': {
                                '@apply lg:justify-between': {},

                                '.dropdown-icon': {
                                    '@apply lg:flex': {},
                                }
                            }
                        },
                        'ul': {
                            '@apply lg:gap-1': {},
                        }
                    },
                    'ul': {
                        '@apply lg:gap-y-4': {},
                    }
                }
            },
            '.sidebar': {
                '@apply hidden lg:block': {},
            }
        },
        '.detached': {
            '.sidebar': {
                '@apply overflow-hidden border rounded-md border-t-white border-black/10 inset-y-4 dark:border-darkborder': {},
                '@apply max-[1024px]:inset-x-[15px]': {},

                '.detached-menu': {
                    '@apply h-[calc(100vh-92px)]': {},
                }
            },
            '.main-container': {
                '@apply flex lg:max-w-[96%] mx-auto': {},
            },
            '.main-content': {
                '@apply p-4 ltr:pr-0 rtl:pl-0': {},
            },
            '.detached-breadcrumb': {
                '@apply mt-4': {},

                'li': {
                    '@apply first:text-white/60 first:dark:text-white last:text-white last:dark:text-white': {},
                }
            },
            '.detache-card-area': {
                '@apply min-h-[calc(100vh-246px)]': {},
            },
            '.detached-content': {
                '@apply p-0 h-[calc(100vh-92px)]': {},
            },
            '.detached-topbar': {
                '@apply sticky z-10 rounded-sm top-4': {},
            },
            '.detached-img': {
                '@apply z-0 block': {},
            },
            '&.detached-simple': {
                '.detached-img': {
                    '@apply hidden': {},
                },
                '.detached-topbar': {
                    '@apply border border-black/10 dark:border-darkborder': {},
                },
                '.sidebar': {
                    '@apply border-t-black/10': {},
                },
                '.detached-breadcrumb': {
                    'li': {
                        '@apply text-black dark:text-white': {},
                    }
                }
            }
        }
    });
});
