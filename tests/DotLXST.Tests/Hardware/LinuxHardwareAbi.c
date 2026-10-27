#include <linux/gpio.h>
#include <linux/i2c-dev.h>
#include <stddef.h>
#include <stdio.h>

int main(void)
{
    printf("{\"request_size\":%zu,\"config_size\":%zu,\"values_size\":%zu,"
           "\"config_offset\":%zu,\"count_offset\":%zu,\"fd_offset\":%zu,"
           "\"request_ioctl\":%lu,\"config_ioctl\":%lu,\"read_ioctl\":%lu,\"write_ioctl\":%lu,"
           "\"i2c_slave_ioctl\":%u}\n",
           sizeof(struct gpio_v2_line_request), sizeof(struct gpio_v2_line_config), sizeof(struct gpio_v2_line_values),
           offsetof(struct gpio_v2_line_request, config), offsetof(struct gpio_v2_line_request, num_lines),
           offsetof(struct gpio_v2_line_request, fd),
           (unsigned long)GPIO_V2_GET_LINE_IOCTL, (unsigned long)GPIO_V2_LINE_SET_CONFIG_IOCTL,
           (unsigned long)GPIO_V2_LINE_GET_VALUES_IOCTL, (unsigned long)GPIO_V2_LINE_SET_VALUES_IOCTL,
           I2C_SLAVE);
    return 0;
}
