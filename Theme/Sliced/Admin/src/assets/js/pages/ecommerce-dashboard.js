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
                formatter: (() => {
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
                formatter: (() => {
                    return "";
                }),
            },
        },
    },
};

var chart = new ApexCharts(document.querySelector("#chart2"), options);
chart.render();

// Chart Widget 3
var options = {
    series: [
        {
            data: [0, 82, 60, 65, 0, 10, 80, 20, 70, 98],
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
                formatter: (() => {
                    return "";
                }),
            },
        },
    },
};

var chart = new ApexCharts(document.querySelector("#chart3"), options);
chart.render();

// Chart Widget 4
var options = {
    series: [
        {
            data: [10, 40, 40, 10, 20, 89, 70, 20, 70, 98],
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
                formatter: (() => {
                    return "";
                }),
            },
        },
    },
};

var chart = new ApexCharts(document.querySelector("#chart4"), options);
chart.render();

// Chart Widget 5
var options = {
    series: [{
        name: 'Net Profit',
        data: [44, 55, 57, 56, 61, 58, 63, 60, 66]
    }, {
        name: 'Revenue',
        data: [76, 85, 101, 98, 87, 105, 91, 114, 94]
    }, {
        name: 'Free Cash Flow',
        data: [35, 41, 36, 26, 45, 48, 52, 53, 41]
    }],
    chart: {
        type: 'bar',
        height: 270
    },
    plotOptions: {
        bar: {
            horizontal: false,
            columnWidth: '55%',
            endingShape: 'rounded'
        },
    },
    dataLabels: {
        enabled: false
    },
    stroke: {
        show: true,
        width: 2,
        colors: ['transparent']
    },
    colors: ["#6f5ef1", "#f1416c", "#50cd89"],
    xaxis: {
        categories: ['Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct'],
    },
    fill: {
        opacity: 1
    },
    tooltip: {
        y: {
            formatter: function (val) {
                return "$ " + val + " thousands"
            }
        }
    }
};

var chart = new ApexCharts(document.querySelector("#chart5"), options);
chart.render();

//chart6
var options = {
    series: [44, 55, 67],
    chart: {
        height: 320,
        type: "donut",
    },
    colors: ["#6a69f5", "#50cd89", "#323a46"],
    legend: {
        position: 'bottom'
    },
    labels: ["Offline", "Online", "Other"],
};

var chart = new ApexCharts(document.querySelector("#chart6"), options);
chart.render();

//top Charts
var options = {
    series: [{
        name: 'Orders',
        data: [20, 47, 40, 30, 50],
    }],
    chart: {
        height: 270,
        type: 'radar',
    },
    dataLabels: {
        enabled: true
    },
    plotOptions: {
        radar: {
            size: 125,
            polygons: {
                strokeColors: '#e9e9e9',
                fill: {
                    colors: ['#f8f8f8', '#fff']
                }
            }
        }
    },
    colors: ['#ffc700'],
    markers: {
        size: 4,
        colors: ['#fff'],
        strokeColor: '#ffc700',
        strokeWidth: 2,
    },
    tooltip: {
        y: {
            formatter: function (val) {
                return val
            }
        }
    },
    xaxis: {
        categories: ['iPhone', 'Macbook', 'iPad', 'Airpods', 'iWatch']
    },
    yaxis: {
        tickAmount: 7,
        labels: {
            formatter: function (val, i) {
                if (i % 2 === 0) {
                    return val
                } else {
                    return ''
                }
            }
        }
    }
};

var chart = new ApexCharts(document.querySelector("#topCharts"), options);
chart.render();