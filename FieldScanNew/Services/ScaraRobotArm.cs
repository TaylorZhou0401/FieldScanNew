using ControlBeanExDll;
using System;
using System.Threading;
using System.Threading.Tasks;
using TcpserverExDll;

namespace FieldScanNew.Services
{
    // SCARA机械臂的“插头”
    public class ScaraRobotArm : IRobotArm
    {
        public string DeviceName => "慧灵科技 Z-Arm 2442";
        public bool IsConnected { get; private set; } = false;
        private ControlBeanEx? _robot;

        public async Task ConnectAsync()
        {
            if (IsConnected) return;
            await Task.Run(() =>
            {
                int robotId = 82;
                TcpserverEx.net_port_initial();
                Thread.Sleep(3000);
                _robot = TcpserverEx.get_robot(robotId);
                for (int i = 0; i < 10; i++)
                {
                    Thread.Sleep(500);
                    if (_robot.is_connected()) break;
                    if (i == 9) throw new TimeoutException("连接机器人超时！");
                }
                int state = _robot.initial(1, 210);
                if (state != 1) throw new Exception("机器人初始化失败！");
                _robot.unlock_position();
                IsConnected = true;
            });
        }

        public void Disconnect()
        {
            if (IsConnected)
            {
                TcpserverEx.close_tcpserver();
                IsConnected = false;
                _robot = null;
            }
        }

        public async Task<RobotPosition> GetPositionAsync()
        {
            if (!IsConnected || _robot == null) throw new InvalidOperationException("机器人未连接");
            return await Task.Run(() =>
            {
                _robot.get_scara_param();
                return new RobotPosition { X = _robot.x, Y = _robot.y, Z = _robot.z, R = _robot.rotation };
            });
        }

        public async Task MoveJogAsync(float stepX, float stepY, float stepZ)
        {
            if (!IsConnected || _robot == null) throw new InvalidOperationException("机器人未连接");
            var currentPos = await GetPositionAsync();
            await Task.Run(() =>
            {
                float targetX = currentPos.X + stepX;
                float targetY = currentPos.Y + stepY;
                float targetZ = currentPos.Z + stepZ;
                _robot.new_movej_xyz_lr(targetX, targetY, targetZ, currentPos.R, 30, 1, targetY > 0 ? 1 : -1);
            });
        }
        public async Task MoveToAsync(float x, float y, float z, float r)
        {
            if (!IsConnected || _robot == null) throw new InvalidOperationException("机器人未连接");

            await Task.Run(() =>
            {
                // 使用较快的速度 (例如 50)
                _robot.new_movej_xyz_lr(x, y, z, r, 50, 1, y > 0 ? 1 : -1);

                // 循环等待，直到机器人到达目标位置
                for (int k = 0; k < 100; k++) // 最长等待约50秒
                {
                    Thread.Sleep(500);
                    if (_robot.is_robot_goto_target())
                    {
                        return; // 到达目标，退出等待
                    }
                }
                throw new TimeoutException("机器人移动超时！");
            });
        }
    }
}