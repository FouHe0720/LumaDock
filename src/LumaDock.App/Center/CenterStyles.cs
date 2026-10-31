using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using ComboBox = System.Windows.Controls.ComboBox;
using CheckBox = System.Windows.Controls.CheckBox;

namespace LumaDock.App.Center;

/// <summary>Small native templates with no frame loop or external visual dependency.</summary>
internal static class CenterStyles
{
    internal static readonly ControlTemplate SliderTemplate = (ControlTemplate)XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Slider">
          <Grid Height="24" Background="Transparent" SnapsToDevicePixels="True">
            <Track x:Name="PART_Track" Orientation="Horizontal" Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}" Value="{Binding Value, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}" IsDirectionReversed="{TemplateBinding IsDirectionReversed}">
              <Track.DecreaseRepeatButton>
                <RepeatButton Focusable="False" Command="{x:Static Slider.DecreaseLarge}" Background="{DynamicResource AccentBrush}">
                  <RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Grid Height="24" Background="Transparent"><Border Height="3" VerticalAlignment="Center" CornerRadius="1.5" Background="{TemplateBinding Background}"/></Grid></ControlTemplate></RepeatButton.Template>
                </RepeatButton>
              </Track.DecreaseRepeatButton>
              <Track.Thumb>
                <Thumb x:Name="Thumb" Width="15" Height="15" Focusable="False" Cursor="Hand">
                  <Thumb.Template><ControlTemplate TargetType="Thumb"><Border x:Name="Handle" CornerRadius="5,8,5,8" Background="{DynamicResource TextBrush}" BorderBrush="{DynamicResource GlassHighlightBrush}" BorderThickness="1"/><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Handle" Property="Background" Value="{DynamicResource AccentBrush}"/></Trigger><Trigger Property="IsDragging" Value="True"><Setter TargetName="Handle" Property="Background" Value="{DynamicResource AccentBrush}"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Thumb.Template>
                </Thumb>
              </Track.Thumb>
              <Track.IncreaseRepeatButton>
                <RepeatButton Focusable="False" Command="{x:Static Slider.IncreaseLarge}" Background="{DynamicResource BorderBrush}">
                  <RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Grid Height="24" Background="Transparent"><Border Height="3" VerticalAlignment="Center" CornerRadius="1.5" Background="{TemplateBinding Background}"/></Grid></ControlTemplate></RepeatButton.Template>
                </RepeatButton>
              </Track.IncreaseRepeatButton>
            </Track>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Thumb" Property="Opacity" Value="0.7"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """);

    internal static readonly ControlTemplate ToggleTemplate = (ControlTemplate)XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="CheckBox">
          <Grid Width="44" Height="26" Background="Transparent" Cursor="Hand">
            <Border x:Name="Track" CornerRadius="8,13,13,8" Background="{DynamicResource ToggleTrackBrush}"/>
            <Border x:Name="Knob" Width="20" Height="20" Margin="3" CornerRadius="6,10,10,6" Background="White" HorizontalAlignment="Left"/>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property="IsChecked" Value="True">
              <Setter TargetName="Track" Property="Background" Value="{DynamicResource AccentBrush}"/>
              <Setter TargetName="Knob" Property="HorizontalAlignment" Value="Right"/>
            </Trigger>
            <Trigger Property="IsMouseOver" Value="True"><Setter Property="Opacity" Value="0.8"/></Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Track" Property="BorderThickness" Value="2"/><Setter TargetName="Track" Property="BorderBrush" Value="{DynamicResource TextBrush}"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """);

    internal static Style ComboStyle() => (Style)XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ComboBox">
          <Setter Property="Foreground" Value="{DynamicResource TextBrush}"/>
          <Setter Property="Background" Value="{DynamicResource PanelBrush}"/>
          <Setter Property="BorderBrush" Value="{DynamicResource GlassHighlightBrush}"/>
          <Setter Property="BorderThickness" Value="1"/>
          <Setter Property="FontSize" Value="12"/>
          <Setter Property="ItemContainerStyle">
            <Setter.Value>
              <Style TargetType="ComboBoxItem">
                <Setter Property="Foreground" Value="{DynamicResource TextBrush}"/>
                <Setter Property="Padding" Value="12,9"/>
                <Setter Property="Template">
                  <Setter.Value><ControlTemplate TargetType="ComboBoxItem">
                    <Border x:Name="Item" CornerRadius="6" Background="Transparent" Padding="{TemplateBinding Padding}" Margin="3,1"><ContentPresenter/></Border>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsHighlighted" Value="True"><Setter TargetName="Item" Property="Background" Value="{DynamicResource BorderBrush}"/></Trigger>
                      <Trigger Property="IsSelected" Value="True"><Setter Property="FontWeight" Value="SemiBold"/></Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate></Setter.Value>
                </Setter>
              </Style>
            </Setter.Value>
          </Setter>
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="ComboBox">
                <Grid>
                  <ToggleButton Focusable="False" IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}" ClickMode="Press" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1">
                    <ToggleButton.Template>
                      <ControlTemplate TargetType="ToggleButton"><Border x:Name="Button" Background="{TemplateBinding Background}" CornerRadius="9,14,9,14" BorderThickness="0.8" BorderBrush="{TemplateBinding BorderBrush}"><Grid><TextBlock Text="⌄" Foreground="{DynamicResource MutedBrush}" HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,12,3"/></Grid></Border><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Button" Property="Opacity" Value="0.75"/></Trigger></ControlTemplate.Triggers></ControlTemplate>
                    </ToggleButton.Template>
                  </ToggleButton>
                  <ContentPresenter Margin="12,8,30,8" IsHitTestVisible="False" VerticalAlignment="Center" Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}" ContentTemplateSelector="{TemplateBinding ItemTemplateSelector}"/>
                  <Popup x:Name="PART_Popup" Placement="Bottom" IsOpen="{TemplateBinding IsDropDownOpen}" AllowsTransparency="True" Focusable="False" PopupAnimation="None">
                    <Border Background="{DynamicResource PopupBrush}" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1" CornerRadius="12" MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}" Padding="4" Margin="0,4,0,0">
                      <ScrollViewer MaxHeight="270" CanContentScroll="True"><ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained"/></ScrollViewer>
                    </Border>
                  </Popup>
                </Grid>
                <ControlTemplate.Triggers><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger></ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """);
}
