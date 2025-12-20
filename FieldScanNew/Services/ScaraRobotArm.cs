using ControlBeanExDll;
using System;
using System.Threading;
using System.Threading.Tasks;
using TcpserverExDll;

namespace FieldScanNew.Services
{
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
                int robotId = 82;//机械臂ID
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
                _robot.set_drag_teach(false);
                IsConnected = true;
            });
        }

        public void Disconnect()
        {
            if (IsConnected)
            {
                _robot?.set_drag_teach(false);
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

        public async Task MoveJogAsync(float stepX, float stepY, float stepZ, float stepR)
        {
            if (!IsConnected || _robot == null) throw new InvalidOperationException("机器人未连接");
            var currentPos = await GetPositionAsync();
            await Task.Run(() =>
            {
                float targetX = currentPos.X + stepX;
                float targetY = currentPos.Y + stepY;
                float targetZ = currentPos.Z + stepZ;
                float targetR = currentPos.R + stepR;
                _robot.new_movej_xyz_lr(targetX, targetY, targetZ, targetR, 30, 1, targetY > 0 ? 1 : -1);
            });
        }

        public async Task MoveToAsync(float x, float y, float z, float r)
        {
            if (!IsConnected || _robot == null) throw new InvalidOperationException("机器人未连接");
            await Task.Run(() =>
            {
                _robot.new_movej_xyz_lr(x, y, z, r, 50, 1, y > 0 ? 1 : -1);
                for (int k = 0; k < 100; k++)
                {
                    Thread.Sleep(500);
                    if (_robot.is_robot_goto_target()) return;
                }
                throw new TimeoutException("机器人移动超时！");
            });
        }

        // **核心修正：实现 MoveToNoWaitAsync**
        // 只发指令，不检查是否到达（专门用于拖动模式下的 Z 轴保持）
        public async Task MoveToNoWaitAsync(float x, float y, float z, float r)
        {
            if (!IsConnected || _robot == null) throw new InvalidOperationException("机器人未连接");
            await Task.Run(() =>
            {
                _robot.new_movej_xyz_lr(x, y, z, r, 30, 1, y > 0 ? 1 : -1);
            });
        }

        public async Task SetDragModeAsync(bool enable)
        {
            if (!IsConnected || _robot == null) throw new InvalidOperationException("机器人未连接");
            await Task.Run(() =>
            {
                bool success = _robot.set_drag_teach(enable);
                if (!success) throw new Exception("切换拖动模式失败，请检查机械臂状态。");
            });
        }
    }
}