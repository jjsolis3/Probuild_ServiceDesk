// Chart Widget 1
var options = {
    series: [
        {
            data: [10, 82, 40, 65, 20, 89, 40, 20, 70, 98],
        },
    ],
    chart: {
        height: 60,
        type: "line",
        fontFamily: "Nunito, sans-serif",
        sparkline: {
            enabled: true,
        },
        dropShadow: {
            enabled: false,
        },
    },
    stroke: {
        curve: "smooth",
        width: 2,
    },
    colors: ["#50cd89"],
    grid: {
        padding: {
            top: 5,
            bottom: 5,
            left: 5,
            right: 5,
        },
    },
    tooltip: {
        x: {
            show: false,
        },
        y: {
            title: {
                formatter: ( () => {
                    return "";
                }),
            },
        },
    },
};

var chart = new ApexCharts(document.querySelector("#chart"), options);
chart.render();

// Chart Widget 2
var options = {
    series: [
        {
            data: [10, 82, 40, 65, 20, 89, 40, 20, 70, 98],
        },
    ],
    chart: {
        height: 60,
        type: "line",
        fontFamily: "Nunito, sans-serif",
        sparkline: {
            enabled: true,
        },
        dropShadow: {
            enabled: false,
        },
    },
    stroke: {
        curve: "smooth",
        width: 2,
    },
    colors: ["#67cc8b"],
    grid: {
        padding: {
            top: 5,
            bottom: 5,
            left: 5,
            right: 5,
        },
    },
    tooltip: {
        x: {
            show: false,
        },
        y: {
            title: {
                formatter: ( () => {
                    return "";
                }),
            },
        },
    },
};

var chart = new ApexCharts(document.querySelector("#chart2"), options);
chart.render();

// Chart Widget 4
var options = {
    series: [
        {
            data: [20, 10, 40, 40, 10, 89, 70, 20, 70, 98, 89, 20, 10],
        },
    ],
    chart: {
        height: 60,
        type: "line",
        fontFamily: "Nunito, sans-serif",
        sparkline: {
            enabled: true,
        },
        dropShadow: {
            enabled: false,
        },
    },
    stroke: {
        curve: "smooth",
        width: 2,
    },
    colors: ["#f1416c"],
    grid: {
        padding: {
            top: 5,
            bottom: 5,
            left: 5,
            right: 5,
        },
    },
    tooltip: {
        x: {
            show: false,
        },
        y: {
            title: {
                formatter: ( () => {
                    return "";
                }),
            },
        },
    },
};

var chart = new ApexCharts(document.querySelector("#chart4"), options);
chart.render();

var options = {
    series: [{
        name: 'Development',
        type: 'line',
        data: [44, 55, 31, 47, 31, 43, 26, 41, 31, 47, 33]
    }, {
        name: 'UX Design',
        type: 'line',
        data: [55, 69, 45, 61, 43, 54, 37, 52, 44, 61, 43]
    }, {
        name: 'Web Design',
        type: 'line',
        data: [54, 37, 52, 69, 45, 61, 43, 44, 61, 43, 55]
    }],
    chart: {
        height: 310,
        type: 'line',
        toolbar: {
            show: false,
        }
    },
    grid: { 
        padding: {
            top: -15,
            right: -2,
            bottom: -10,
        },
    },
    stroke: {
        curve: 'smooth',
        width: [2, 2, 2]
    },
    fill: {
        type: 'solid',
        opacity: [0.35, 1],
    },
    labels: ['Dec 01', 'Dec 02', 'Dec 03', 'Dec 04', 'Dec 05', 'Dec 06', 'Dec 07', 'Dec 08', 'Dec 09 ', 'Dec 10', 'Dec 11'],
    markers: {
        size: 0
    },
    tooltip: {
        shared: true,
        intersect: false,
        y: {
            formatter: function (y) {
                if (typeof y !== "undefined") {
                    return y.toFixed(0) + " Task";
                }
                return y;
            }
        }
    }
};

var chart = new ApexCharts(document.querySelector("#chart5"), options);
chart.render();